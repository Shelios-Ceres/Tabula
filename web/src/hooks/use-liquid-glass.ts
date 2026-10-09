import { useEffect, useLayoutEffect, useRef } from "react";

import { useLiquidGlassParams, type LiquidGlassParams } from "@/stores/use-liquid-glass-store";

// Liquid Glass：移植 LiquidGlassWinUI 的 LiquidGlass.hlsl 材质（折射带 + 光谱色散 + 菲涅尔边光 + 方向性眩光）。
// 在 AppProviders 里全局调用一次，自动给带 liquid-glass 类的元素和 antd 浮层挂上按自身尺寸生成的材质：
// - 实时玻璃（浮在内容上的胶囊、浮层、菜单）：SVG 滤镜实时折射背景。只带 liquid-glass 的是通透档（R/G/B 分别折射出色散）；
//   带 liquid-glass-regular 的元素和 antd 浮层是常规档（模糊较强、看不出色散，只折射一次以减少每帧开销）。
// - 静态玻璃（贴边栏 data-glass-edge、页面里的 liquid-glass-static 面板）：背后是纯色背景，折射看不出来，只叠计算出的边光，
//   不用 backdrop-filter，没有每帧开销；贴边栏只在 data-glass-edge 标出的那条边上打光。
// 背景模糊/曝光/对比度/饱和度在 url() 之前用 CSS 滤镜完成，对应 WinUI 的 GaussianBlur 与 PostProcessing。
// 只有 Chromium 支持 backdrop-filter 中的 url(#filter)，其他浏览器的实时玻璃回退为 CSS 模糊玻璃。
const SVG_NS = "http://www.w3.org/2000/svg";
const SUPPORTS_SVG_BACKDROP = typeof navigator !== "undefined" && /Chrome\//.test(navigator.userAgent);
const OVERLAY_SELECTOR = ".ant-modal-container, .ant-popover-container, .ant-dropdown-menu, .ant-select-dropdown, .ant-drawer-section, .ant-message-notice";
const SURFACE_SELECTOR = `.liquid-glass, ${OVERLAY_SELECTOR}`;
const DEG = Math.PI / 180;
// 7 抽头光谱采样折算成单抽头时 R/G/B 的平均位置（红偏外、蓝偏内），系数来自着色器的光谱权重
const SPECTRUM_SHIFTS = [3 / 7, 2 / 21, -0.5];
const EDGE_NORMALS = { top: [0, -1], right: [1, 0], bottom: [0, 1], left: [-1, 0] } as const;
const DRAWER_EDGES = { left: "right", right: "left", top: "bottom", bottom: "top" } as const;
const STRENGTH_HALF_MAX = 28; // 折射偏移强度最多按 28px（画布最高胶囊的一半）计算，避免大面板边缘偏移过大
const MAP_PIXEL_LIMIT = 300_000; // 单张贴图最多约 30 万像素，大面板按比例降低贴图分辨率（只影响边缘过渡的细腻程度）
const IDLE_ASSET_LIMIT = 32; // 暂时没人用的贴图/滤镜最多缓存数量，重新打开同尺寸浮层时直接复用

type Tier = "clear" | "regular";
type Edge = keyof typeof EDGE_NORMALS;
type Surface = { tier: Tier; live: boolean; edge?: Edge; width: number; height: number; radius: number; resolution: number; params: LiquidGlassParams };
type Asset = { light: string; id?: string; filter?: SVGFilterElement; users: number };

const assets = new Map<string, Asset>();
const idleKeys: string[] = [];
let filterSeed = 0;

// Safari 等没有 requestIdleCallback 的浏览器用定时器代替（静态玻璃在所有浏览器都会挂载）
const whenIdle = (callback: () => void, timeout?: number) => ("requestIdleCallback" in window ? requestIdleCallback(callback, timeout ? { timeout } : undefined) : window.setTimeout(callback, timeout ?? 50));
const cancelIdle = (id: number) => (id && "cancelIdleCallback" in window ? cancelIdleCallback(id) : window.clearTimeout(id));
const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));
const smoothstep = (edge0: number, edge1: number, value: number) => {
    const t = clamp((value - edge0) / (edge1 - edge0), 0, 1);
    return t * t * (3 - 2 * t);
};

function getSvgRoot() {
    const existing = document.getElementById("liquid-glass-filters");
    if (existing) return existing;
    const root = document.createElementNS(SVG_NS, "svg");
    root.id = "liquid-glass-filters";
    root.setAttribute("aria-hidden", "true");
    root.style.cssText = "position:absolute;width:0;height:0;overflow:hidden;pointer-events:none";
    return document.body.appendChild(root);
}

/** 圆角矩形 SDF：返回距边缘的内部深度与外法线。 */
function roundedRectField(px: number, py: number, hw: number, hh: number, r: number) {
    const qx = Math.abs(px) - (hw - r);
    const qy = Math.abs(py) - (hh - r);
    const sx = Math.sign(px) || 1;
    const sy = Math.sign(py) || 1;
    if (qx > 0 && qy > 0) {
        const len = Math.hypot(qx, qy) || 1;
        return { depth: r - len, nx: (qx / len) * sx, ny: (qy / len) * sy };
    }
    if (qx > qy) return { depth: r - qx, nx: sx, ny: 0 };
    return { depth: r - qy, nx: 0, ny: sy };
}

/** 贴边栏：深度只取到自由边的距离，法线固定朝外。 */
function edgeField(edge: Edge, x: number, y: number, width: number, height: number) {
    const [nx, ny] = EDGE_NORMALS[edge];
    return { depth: edge === "top" ? y : edge === "bottom" ? height - y : edge === "left" ? x : width - x, nx, ny };
}

/** 生成高光贴图；实时玻璃再生成位移贴图（通透档 R/G/B 三张含色散，常规档一张）。 */
function createMaps({ tier, live, edge, width, height, radius, resolution, params }: Surface) {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    // 贴边栏只有一条自由边，贴图只沿垂直于这条边的方向变化：画成 1px 宽的条带再拉伸，与栏的长度无关
    const across = edge === "left" || edge === "right";
    const w = edge && !across ? 1 : Math.round(width * resolution);
    const h = edge && across ? 1 : Math.round(height * resolution);
    const hw = width / 2;
    const hh = height / 2;
    const r = edge ? 0 : Math.min(radius, hw, hh);
    const depthLimit = edge ? (across ? width : height) : Math.min(hw, hh);
    const shifts = !live ? [] : tier === "clear" && !edge ? SPECTRUM_SHIFTS : [0];
    const thickness = Math.max(0.01, Math.min(params.refThickness, depthLimit));
    const strength = params.refStrength * Math.min(depthLimit, STRENGTH_HALF_MAX);
    const maxDispersion = shifts.length > 1 ? clamp(params.clearBlur * 0.5, 2, 8) : 0;
    // 位移按最大可能偏移归一化：最外圈偏折为 tan(90° − asin(1/n)) = √(n² − 1)，再加色散半宽
    const maxShift = Math.max(0.01, strength * Math.sqrt(Math.max(params.refFactor ** 2 - 1, 0)) + maxDispersion / 2);
    const fresnelK = Math.min((500 / Math.max(params.fresnelRange, 1e-4)) ** 2, 3000);
    const glareK = Math.min((500 / Math.max(params.glareRange, 1e-4)) ** 2, 3000);
    const fresnelWeight = (params.fresnelFactor / 100) * 0.7 * smoothstep(0, 2, params.fresnelRange * dpr);
    const glareCoverage = smoothstep(0, 2, params.glareRange * dpr);
    const normalLength = 1414.2 / ((edge ? depthLimit : height) * dpr);
    const glareAngle = params.glareAngle * DEG;
    // 折射与打光只发生在贴边一圈，更里面保持中性值：逐像素只算这一圈（圆角范围内整块计算）
    const band = Math.max(thickness, ((1 + params.fresnelHardness / 100) * 1500) / fresnelK, ((1 + params.glareHardness / 100) * 1500) / glareK);
    const span = Math.ceil(Math.max(band, r) * resolution) + 1;
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext("2d")!;
    const maps = shifts.map(() => {
        const image = ctx.createImageData(w, h);
        new Uint32Array(image.data.buffer).fill(0xff808080);
        return image;
    });
    const light = ctx.createImageData(w, h);
    // 采样点限制在元素范围内，对应着色器的 ClampSamplingUv
    const shift = (position: number, offset: number, size: number) => (offset ? clamp(position + offset, 0.5, size - 0.5) - position : 0);

    for (let y = 0; y < h; y++) {
        const fullRow = y < span || y >= h - span;
        for (let x = 0; x < w; x++) {
            if (!fullRow && x === span && w - span > span) x = w - span;
            const cx = (x + 0.5) / resolution;
            const cy = (y + 0.5) / resolution;
            const { depth, nx, ny } = edge ? edgeField(edge, cx, cy, width, height) : roundedRectField(cx - hw, cy - hh, hw, hh, r);
            if (depth < 0 || depth >= band) continue;
            const i = (y * w + x) * 4;
            if (maps.length && depth < thickness) {
                // 折射：θi = asin(x²)，按折射率得到 θt，偏移沿法线向内
                const xR = 1 - depth / thickness;
                const thetaI = Math.asin(Math.min(1, xR * xR));
                const thetaT = Math.asin(Math.min(1, Math.sin(thetaI) / params.refFactor));
                const edgeFactor = Math.tan(thetaI - thetaT);
                const offX = -nx * edgeFactor * strength;
                const offY = -ny * edgeFactor * strength;
                // 色散：随深度衰减，按象限 (x·y) 决定方向，半宽不超过模糊足迹
                let dispX = 0;
                let dispY = 0;
                if (maxDispersion) {
                    const attenuation = 1 - smoothstep(0, 1, depth / thickness / Math.max(params.dispersionRange, 0.001));
                    const amount = params.refDispersion * attenuation * (((cx - hw) * (cy - hh)) / (hw * hh));
                    const length = Math.hypot(offX * amount, offY * amount);
                    const limit = length > maxDispersion ? maxDispersion / length : 1;
                    dispX = offX * amount * limit;
                    dispY = offY * amount * limit;
                }
                maps.forEach((map, index) => {
                    map.data[i] = 128 + (shift(cx, offX + dispX * shifts[index], width) * 127) / maxShift;
                    map.data[i + 1] = 128 + (shift(cy, offY + dispY * shifts[index], height) * 127) / maxShift;
                });
            }
            // 菲涅尔边光：整圈贴边、随深度快速衰减
            const fresnel = clamp(Math.max(1 - (depth * fresnelK) / 1500 + params.fresnelHardness / 100, 0) ** 5, 0, 1) * fresnelWeight;
            // 方向性眩光：按法线角度与眩光方向计算，近侧强、对侧弱
            const glareGeo = clamp(Math.max(1 - (depth * glareK) / 1500 + params.glareHardness / 100, 0) ** 5, 0, 1);
            let normalAngle = Math.atan2(ny, nx);
            if (normalAngle < 0) normalAngle += Math.PI * 2;
            const angle = (normalAngle - Math.PI / 4 + glareAngle) * 2;
            const farSide = (angle > 1.5 * Math.PI && angle < 3.5 * Math.PI) || angle < -0.5 * Math.PI;
            const angleFactor = (0.5 + Math.sin(angle) * 0.5) * (farSide ? 1.2 * (params.glareOpposite / 100) : 1.2) * (params.glareFactor / 100);
            const glare = clamp(Math.max(angleFactor, 0) ** (0.1 + (params.glareConvergence / 100) * 2), 0, 1) * glareGeo;
            light.data[i] = 255;
            light.data[i + 1] = 255;
            light.data[i + 2] = 255;
            light.data[i + 3] = (1 - (1 - fresnel) * (1 - glare * clamp(glare * normalLength, 0, 1) * glareCoverage)) * 255;
        }
    }

    const encode = (image: ImageData) => {
        ctx.putImageData(image, 0, 0);
        return canvas.toDataURL();
    };
    return { maps: maps.map(encode), light: encode(light), displacement: maxShift * 2 };
}

function createFilter(id: string, { tier, width, height }: Surface, { maps, light, displacement }: ReturnType<typeof createMaps>) {
    const filter = document.createElementNS(SVG_NS, "filter");
    filter.id = id;
    Object.entries({ x: 0, y: 0, width, height, filterUnits: "userSpaceOnUse", primitiveUnits: "userSpaceOnUse", "color-interpolation-filters": "sRGB" }).forEach(([name, value]) => filter.setAttribute(name, String(value)));
    const image = (href: string, result: string) => `<feImage href="${href}" x="0" y="0" width="${width}" height="${height}" preserveAspectRatio="none" result="${result}" />`;
    const displace = (map: string, result: string) => `<feDisplacementMap in="SourceGraphic" in2="${map}" scale="${displacement}" xChannelSelector="R" yChannelSelector="G" result="${result}" />`;
    const channel = (index: number, matrix: string) => `${image(maps[index], `map-${index}`)}${displace(`map-${index}`, `raw-${index}`)}<feColorMatrix in="raw-${index}" type="matrix" values="${matrix}" result="channel-${index}" />`;
    // 通透档 R/G/B 各自折射后合成（色散）；常规档只折射一次
    const refraction =
        maps.length === 3
            ? `${channel(0, "1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0")}${channel(1, "0 0 0 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 1 0")}${channel(2, "0 0 0 0 0 0 0 0 0 0 0 0 1 0 0 0 0 0 1 0")}
               <feBlend in="channel-0" in2="channel-1" mode="screen" result="rg" /><feBlend in="rg" in2="channel-2" mode="screen" result="refracted" />`
            : `${image(maps[0], "map")}${displace("map", "refracted")}`;
    // 折射 → 叠玻璃染色（CSS 变量，跟随主题）→ 叠菲涅尔边光与眩光
    filter.innerHTML = `${refraction}${image(light, "light")}
        <feFlood style="flood-color: var(${tier === "clear" ? "--liquid-tint" : "--liquid-regular-tint"})" result="tint" />
        <feComposite in="tint" in2="refracted" operator="over" result="body" />
        <feComposite in="light" in2="body" operator="over" />`;
    getSvgRoot().appendChild(filter);
    return filter;
}

/** 同尺寸、同参数的表面共用一份贴图/滤镜；没人用的先缓存，超出数量再删除。 */
function acquireAsset(key: string, surface: Surface) {
    let asset = assets.get(key);
    if (!asset) {
        const maps = createMaps(surface);
        asset = { light: maps.light, users: 0 };
        if (surface.live) {
            asset.id = `liquid-glass-${++filterSeed}`;
            asset.filter = createFilter(asset.id, surface, maps);
        }
        assets.set(key, asset);
    } else if (!asset.users) idleKeys.splice(idleKeys.indexOf(key), 1);
    asset.users += 1;
    return asset;
}

function releaseAsset(key: string) {
    const asset = assets.get(key);
    if (!asset) return;
    asset.users -= 1;
    if (asset.users) return;
    idleKeys.push(key);
    idleKeys.splice(0, idleKeys.length - IDLE_ASSET_LIMIT).forEach((stale) => {
        assets.get(stale)?.filter?.remove();
        assets.delete(stale);
    });
}

/** 给一个表面挂上材质：首次出现时生成；尺寸或参数变化时先拉伸现有材质，主线程空闲时再换成精确贴图。 */
function attachSurface(el: HTMLElement, getParams: () => LiquidGlassParams) {
    const live = !el.dataset.glassEdge && !el.classList.contains("liquid-glass-static");
    // 只带 liquid-glass 的实时玻璃是通透档；带 liquid-glass-regular 的、antd 浮层和静态玻璃都是常规档
    const tier: Tier = live && el.classList.contains("liquid-glass") && !el.classList.contains("liquid-glass-regular") ? "clear" : "regular";
    const placement = el.closest(".ant-drawer")?.className.match(/\bant-drawer-(left|right|top|bottom)\b/)?.[1] as keyof typeof DRAWER_EDGES | undefined;
    const edge = (el.dataset.glassEdge as Edge | undefined) ?? (placement && DRAWER_EDGES[placement]);
    let key = "";
    let adjusted = false;
    let stretched: SVGFilterElement | undefined;
    let frame = 0;
    let idle = 0;

    // 背景处理链：恒等的对比度、饱和度不写进去，少一道滤镜
    const backdrop = () => {
        const params = getParams();
        return [`blur(${tier === "clear" ? params.clearBlur : params.regularBlur}px)`, `brightness(${params.exposure})`, params.contrast !== 1 && `contrast(${params.contrast})`, params.saturate !== 1 && `saturate(${params.saturate})`].filter(Boolean).join(" ");
    };
    const show = () => {
        const asset = assets.get(key);
        if (!asset) return;
        stretched?.remove();
        stretched = undefined;
        adjusted = false;
        if (!live) return el.style.setProperty("--lg-light", `url(${asset.light})`);
        el.style.setProperty("--lg-filter", `${backdrop()} url(#${asset.id})`);
        el.style.setProperty("--lg-bg", "transparent");
    };
    // 新贴图生成前的过渡：已有材质的实时玻璃把当前滤镜复制一份拉伸到新尺寸（静态玻璃的边光背景本来就会拉伸），
    // 第一次出现的实时玻璃先只用同样的模糊和 CSS 染色，避免玻璃轮廓闪一下
    const bridge = (width: number, height: number) => {
        if (!live) return;
        adjusted = true;
        const asset = assets.get(key);
        if (!asset?.filter) {
            el.style.setProperty("--lg-filter", backdrop());
            el.style.removeProperty("--lg-bg");
            return;
        }
        if (!stretched) {
            stretched = asset.filter.cloneNode(true) as SVGFilterElement;
            stretched.id = `liquid-glass-${++filterSeed}`;
            getSvgRoot().appendChild(stretched);
        }
        [stretched, ...stretched.querySelectorAll("feImage")].forEach((node) => {
            node.setAttribute("width", String(width));
            node.setAttribute("height", String(height));
        });
        el.style.setProperty("--lg-filter", `${backdrop()} url(#${stretched.id})`);
        el.style.setProperty("--lg-bg", "transparent");
    };
    const measure = () => {
        const width = el.offsetWidth;
        const height = el.offsetHeight;
        if (!width || !height) return undefined;
        const params = getParams();
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const radius = edge ? 0 : Number.parseFloat(getComputedStyle(el).borderTopLeftRadius) || 0;
        const resolution = edge ? dpr : Math.min(dpr, Math.sqrt(MAP_PIXEL_LIMIT / (width * height)));
        // 静态贴边栏的边光与栏的长度无关，长度变化（比如旁边的面板展开）不用重算
        const across = edge === "left" || edge === "right";
        const keySize = !live && edge ? (across ? width : height) : `${width}x${height}`;
        const surface: Surface = { tier, live, edge, width, height, radius, resolution, params };
        return { key: [live ? tier : "static", edge, keySize, radius, resolution, JSON.stringify(params)].join("|"), surface };
    };
    const apply = (next: { key: string; surface: Surface }) => {
        acquireAsset(next.key, next.surface);
        if (key) releaseAsset(key);
        key = next.key;
        show();
    };
    const update = () => {
        cancelIdle(idle);
        idle = 0;
        const next = measure();
        // 隐藏（关闭的浮层）时保留当前材质，重新打开同尺寸时无需重算
        if (!next) return;
        if (next.key === key) return void (adjusted && show());
        if (assets.has(next.key)) return apply(next);
        // 静态玻璃第一次出现时直接生成（开销小）；其余情况在主线程空闲时生成：
        // 连续缩放（动画、拖拽）期间每次更新都会取消上一次，停下来后只生成一次
        if (!key && !live) return apply(next);
        bridge(next.surface.width, next.surface.height);
        idle = whenIdle(
            () => {
                idle = 0;
                apply(next);
            },
            key ? undefined : 200,
        );
    };
    const observer = new ResizeObserver(update);
    observer.observe(el);
    update();

    return {
        refresh() {
            cancelAnimationFrame(frame);
            frame = requestAnimationFrame(update);
        },
        destroy() {
            observer.disconnect();
            cancelAnimationFrame(frame);
            cancelIdle(idle);
            stretched?.remove();
            if (key) releaseAsset(key);
            el.style.removeProperty("--lg-filter");
            el.style.removeProperty("--lg-bg");
            el.style.removeProperty("--lg-light");
        },
    };
}

/** 染色、投影和常规档回退滤镜写成 CSS 变量，供 .liquid-glass、antd 浮层和滤镜里的 feFlood 使用，深浅色跟随 .dark 切换。 */
function syncVariables(p: LiquidGlassParams) {
    let style = document.getElementById("liquid-glass-variables");
    if (!style) {
        style = document.createElement("style");
        style.id = "liquid-glass-variables";
        document.head.appendChild(style);
    }
    const fallback = `blur(${p.regularBlur}px) brightness(${p.exposure}) contrast(${p.contrast}) saturate(${p.saturate})`;
    style.textContent =
        `:root{--liquid-tint:rgba(255,255,255,${p.clearTintLight});--liquid-shadow:0 6px 24px rgba(0,0,0,${p.clearShadowLight});` +
        `--liquid-regular-tint:rgba(255,255,255,${p.regularTintLight});--liquid-regular-shadow:0 12px 40px rgba(0,0,0,${p.regularShadowLight});--liquid-regular-fallback:${fallback}}` +
        `.dark{--liquid-tint:rgba(30,30,32,${p.clearTintDark});--liquid-shadow:0 6px 24px rgba(0,0,0,${p.clearShadowDark});` +
        `--liquid-regular-tint:rgba(30,30,32,${p.regularTintDark});--liquid-regular-shadow:0 12px 40px rgba(0,0,0,${p.regularShadowDark})}`;
}

/** 在 AppProviders 中调用一次：同步材质变量，并自动给页面上所有液态玻璃表面挂上材质。 */
export function useLiquidGlass() {
    const params = useLiquidGlassParams();
    const paramsRef = useRef(params);
    const surfacesRef = useRef(new Map<HTMLElement, ReturnType<typeof attachSurface>>());

    useLayoutEffect(() => {
        paramsRef.current = params;
        syncVariables(params);
        surfacesRef.current.forEach((surface) => surface.refresh());
    }, [params]);

    useEffect(() => {
        const surfaces = surfacesRef.current;
        const attach = (el: Element) => {
            if (!(el instanceof HTMLElement) || surfaces.has(el) || !el.matches(SURFACE_SELECTOR)) return;
            // 嵌在玻璃里的浮层取不到页面背景，留给 CSS 用实色；不支持 SVG 滤镜的浏览器只挂静态玻璃
            if (el.parentElement?.closest(SURFACE_SELECTOR) || (!SUPPORTS_SVG_BACKDROP && !el.dataset.glassEdge && !el.classList.contains("liquid-glass-static"))) return;
            surfaces.set(el, attachSurface(el, () => paramsRef.current));
        };
        const observer = new MutationObserver((records) => {
            for (const record of records) {
                if (record.type === "attributes") attach(record.target as Element);
                record.addedNodes.forEach((node) => {
                    if (!(node instanceof Element)) return;
                    attach(node);
                    node.querySelectorAll(SURFACE_SELECTOR).forEach(attach);
                });
            }
            surfaces.forEach((surface, el) => {
                if (el.isConnected && el.matches(SURFACE_SELECTOR)) return;
                surface.destroy();
                surfaces.delete(el);
            });
        });
        observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class"] });
        document.querySelectorAll(SURFACE_SELECTOR).forEach(attach);
        return () => {
            observer.disconnect();
            surfaces.forEach((surface) => surface.destroy());
            surfaces.clear();
        };
    }, []);
}
