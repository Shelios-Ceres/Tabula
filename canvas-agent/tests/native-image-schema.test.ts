import { expect, test } from "bun:test";
import { toolInputSchemas } from "../src/canvas/schemas";

test("native image tools retain Endpoint bindings and reject inverted rectangles", () => {
    const model = "ark::doubao-seedream-5-0-pro-260628";
    const parsed = toolInputSchemas.canvas_generate_image.parse({ prompt: "draw", model, imageNative: { ark: { endpoint: { id: "ep-pro", model }, watermark: false } } });
    expect(parsed.imageNative?.ark?.endpoint?.id).toBe("ep-pro");
    expect(toolInputSchemas.canvas_generate_image.safeParse({ prompt: "edit", imageSelections: [{ kind: "rectangle", x1: .8, y1: .7, x2: .2, y2: .3 }] }).success).toBe(false);
});
