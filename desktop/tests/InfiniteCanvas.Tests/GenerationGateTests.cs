using InfiniteCanvas.Core;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Tests;

/// <summary>The gate is static and shared by every canvas, so each test uses a channel ID of its own.</summary>
public class GenerationGateTests
{
    private static string Channel() => Guid.NewGuid().ToString("N");

    private static Task<IDisposable> Enter(string channel, int? limit, int weight = 1, Action<int?>? queued = null, CancellationToken ct = default) =>
        GenerationGate.EnterAsync(channel, limit, weight, queued, ct);

    [Fact]
    public async Task A_request_over_the_limit_waits_and_is_told_it_is_next_until_a_slot_frees()
    {
        var channel = Channel();
        var first = await Enter(channel, 2);
        await Enter(channel, 2);
        var told = new List<int?>();
        var third = Enter(channel, 2, queued: told.Add);

        Assert.False(third.IsCompleted);
        Assert.Equal([0], told);
        first.Dispose();
        await third;
        Assert.Equal([0, null], told);
    }

    [Fact]
    public async Task A_4K_request_takes_two_slots()
    {
        var channel = Channel();
        for (var i = 0; i < 4; i++) await Enter(channel, 8, GenerationGate.Weight("4K"));
        var fifth = Enter(channel, 8, GenerationGate.Weight("4K"));
        var small = Enter(channel, 8);

        Assert.False(fifth.IsCompleted);
        Assert.False(small.IsCompleted);
        Assert.Equal((2, 2, 1, 1), (GenerationGate.Weight("4K"), GenerationGate.Weight("4k"), GenerationGate.Weight("2K"), GenerationGate.Weight(null)));
    }

    [Fact]
    public async Task Requests_are_served_strictly_in_order_so_a_big_one_is_not_starved()
    {
        var channel = Channel();
        var a = await Enter(channel, 2);
        var b = await Enter(channel, 2);
        var big = Enter(channel, 2, 2);
        var small = Enter(channel, 2);

        a.Dispose();
        Assert.False(big.IsCompleted);
        Assert.False(small.IsCompleted);
        b.Dispose();
        var bigSlot = await big;
        Assert.False(small.IsCompleted);
        bigSlot.Dispose();
        await small;
    }

    [Fact]
    public async Task A_cancelled_request_leaves_the_queue_and_the_ones_behind_move_up()
    {
        var channel = Channel();
        var held = await Enter(channel, 1);
        using var cancel = new CancellationTokenSource();
        var second = Enter(channel, 1, ct: cancel.Token);
        var told = new List<int?>();
        var third = Enter(channel, 1, queued: told.Add);
        Assert.Equal([1], told);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal([1, 0], told);
        held.Dispose();
        await third;
    }

    [Fact]
    public async Task A_request_whose_token_is_already_cancelled_never_queues()
    {
        var channel = Channel();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Enter(channel, 1, ct: cancel.Token));
        await Enter(channel, 1);
    }

    [Fact]
    public async Task No_limit_never_waits()
    {
        var channel = Channel();
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Enter(channel, null, 2)));
        Assert.Equal(20, all.Length);
    }

    [Fact]
    public async Task A_weight_above_the_limit_still_runs_alone()
    {
        var channel = Channel();
        var big = await Enter(channel, 1, 2);
        var next = Enter(channel, 1);
        Assert.False(next.IsCompleted);
        big.Dispose();
        await next;
    }

    [Fact]
    public async Task Disposing_a_slot_twice_releases_it_once()
    {
        var channel = Channel();
        var first = await Enter(channel, 1);
        var second = Enter(channel, 1);
        var third = Enter(channel, 1);
        first.Dispose();
        first.Dispose();
        var slot = await second;
        Assert.False(third.IsCompleted);
        slot.Dispose();
        await third;
    }

    [Fact]
    public async Task Channels_do_not_affect_each_other()
    {
        var (a, b) = (Channel(), Channel());
        await Enter(a, 1);
        Assert.True(Enter(a, 1).IsCompleted == false);
        await Enter(b, 1);
    }

    [Fact]
    public async Task Raising_the_limit_lets_waiting_requests_through()
    {
        var channel = Channel();
        await Enter(channel, 1);
        var waiting = Enter(channel, 1);
        Assert.False(waiting.IsCompleted);

        var raised = await Enter(channel, 3);
        await waiting;
        Assert.NotNull(raised);
    }

    [Fact]
    public async Task Clearing_the_limit_lets_waiting_requests_through()
    {
        var channel = Channel();
        await Enter(channel, 1);
        var waiting = Enter(channel, 1);
        await Enter(channel, null);
        await waiting;
    }

    [Fact]
    public void Channel_limit_defaults_to_eight_and_an_empty_one_survives_a_save()
    {
        Assert.Equal(8, Json.Deserialize<ModelChannel>("""{"id":"c","vendor":"OpenAI"}""")!.MaxConcurrency);
        Assert.Equal(3, Json.Deserialize<ModelChannel>("""{"id":"c","maxConcurrency":3}""")!.MaxConcurrency);
        Assert.Null(Json.Deserialize<ModelChannel>("""{"id":"c","maxConcurrency":null}""")!.MaxConcurrency);
        // Null is written out: leaving it out would read back as the default 8.
        Assert.Null(Json.Deserialize<ModelChannel>(Json.Serialize(new ModelChannel { MaxConcurrency = null }))!.MaxConcurrency);
        Assert.Equal(8, Json.Deserialize<ModelChannel>(Json.Serialize(new ModelChannel()))!.MaxConcurrency);
    }

    [Fact]
    public void Replacing_a_picture_drops_what_described_the_old_one_and_keeps_where_the_node_belongs()
    {
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        foreach (var key in NodeKeys.OfPixels) node.SetOption(key, "x");
        foreach (var key in new[] { NodeKeys.SourceId, NodeKeys.Stack, NodeKeys.StackOpen, "background" }) node.SetOption(key, "keep");

        NodeKeys.ForgetPixels(node);

        Assert.All(new[] { NodeKeys.Mask, NodeKeys.Markup, NodeKeys.Selections, NodeKeys.ConversationId, NodeKeys.ConversationModel, NodeKeys.ModelText, NodeKeys.InputNote }, key => Assert.Equal("", node.Option(key)));
        Assert.All(new[] { NodeKeys.SourceId, NodeKeys.Stack, NodeKeys.StackOpen, "background" }, key => Assert.Equal("keep", node.Option(key)));
    }
}
