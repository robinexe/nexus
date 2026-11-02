using Nexus.Desktop.Services.Shell;

namespace Nexus.Tests;

/// <summary>Lógica pura da casca: vigia da interface (suspensão não é travamento) .</summary>
[Trait("Category", "Unit")]
public sealed class ShellLogicTests
{
    // ------------------------------------------------------------------ StallDetector

    /// <summary>Simula o vigia ticando a cada 1 s, com a interface respondendo (pong) ou não.</summary>
    private sealed class Clock
    {
        public long Wall, Awake;
        public void Advance(long ms, bool sleeping = false) { Wall += ms; if (!sleeping) Awake += ms; }
    }

    [Fact]
    public void Responsive_ui_never_reports()
    {
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        for (var i = 0; i < 30; i++)
        {
            c.Advance(1000);
            Assert.Equal(StallEventKind.None, d.Tick(c.Wall, c.Awake).Kind);
            Assert.Null(d.Pong(c.Awake));
        }
    }

    [Fact]
    public void Real_hang_is_reported_once_and_recovery_has_duration()
    {
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        c.Advance(1000); d.Tick(c.Wall, c.Awake); d.Pong(c.Awake);
        var events = new List<StallEvent>();
        for (var i = 0; i < 6; i++) { c.Advance(1000); events.Add(d.Tick(c.Wall, c.Awake)); } // interface sem responder 6 s
        var stalled = Assert.Single(events, e => e.Kind == StallEventKind.Stalled);
        Assert.Equal(3.0, stalled.Seconds, 3);
        Assert.True(d.Reported);
        Assert.Equal(6.0, d.Pong(c.Awake)!.Value, 3);
        Assert.False(d.Reported);
    }

    [Fact]
    public void Sleep_excluded_by_unbiased_clock_is_not_a_hang()
    {
        // O caso do PC real: "INTERFACE PARADA há 9629 s" logo depois de voltar da suspensão.
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        c.Advance(1000); d.Tick(c.Wall, c.Awake); d.Pong(c.Awake);
        c.Advance(9_629_000, sleeping: true);
        var e = d.Tick(c.Wall, c.Awake);
        Assert.Equal(StallEventKind.ResumedFromSleep, e.Kind);
        Assert.Equal(9629, e.Seconds, 0);
        // Logo depois, a interface ainda não respondeu ao primeiro pong: não é travamento.
        c.Advance(1000);
        Assert.Equal(StallEventKind.None, d.Tick(c.Wall, c.Awake).Kind);
        Assert.Null(d.Pong(c.Awake));
    }

    [Fact]
    public void Frozen_process_with_awake_clock_running_is_not_a_hang()
    {
        // Modern Standby: o relógio "acordado" pode continuar andando, mas o vigia (timer do pool) ficou sem tiques.
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        c.Advance(1000); d.Tick(c.Wall, c.Awake); d.Pong(c.Awake);
        c.Advance(1_038_000);
        Assert.Equal(StallEventKind.ResumedFromSleep, d.Tick(c.Wall, c.Awake).Kind);
        c.Advance(1000);
        Assert.Equal(StallEventKind.None, d.Tick(c.Wall, c.Awake).Kind);
    }

    [Fact]
    public void Hang_in_progress_is_cleared_by_power_resume_and_counts_again_after()
    {
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        for (var i = 0; i < 4; i++) { c.Advance(1000); d.Tick(c.Wall, c.Awake); }
        Assert.True(d.Reported);
        d.ResetAt(c.Awake); // PowerModeChanged
        Assert.False(d.Reported);
        Assert.Null(d.Pong(c.Awake));
        // Um travamento novo depois disso ainda é visto.
        for (var i = 0; i < 3; i++) { c.Advance(1000); d.Tick(c.Wall, c.Awake); }
        Assert.True(d.Reported);
    }

    [Fact]
    public void Small_timer_jitter_is_not_sleep()
    {
        var c = new Clock();
        var d = new StallDetector(c.Awake);
        c.Advance(1000); d.Tick(c.Wall, c.Awake); d.Pong(c.Awake);
        c.Advance(4900); // timer atrasado, abaixo do limite de 5 s
        var e = d.Tick(c.Wall, c.Awake);
        Assert.Equal(StallEventKind.Stalled, e.Kind); // a interface também não respondeu nesse tempo
    }
}
