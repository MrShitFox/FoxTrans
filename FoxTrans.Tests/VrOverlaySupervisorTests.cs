using Xunit;

public sealed class VrOverlaySupervisorTests
{
    private static readonly VrOverlayPlacement Placement = new(
        0.85f, 1.05f, -18, 0, -0.18f, 0.92f);

    [Fact]
    public void DisabledSupervisorDoesNotTouchSteamVr()
    {
        var device = new FakeVrOverlayDevice { Available = true };
        using var supervisor = new VrOverlaySupervisor(device);

        supervisor.Tick(Placement);

        Assert.Equal(VrOverlayStatus.Disabled, supervisor.Status);
        Assert.Equal(0, device.AvailabilityChecks);
        Assert.Equal(0, device.OpenCalls);
    }

    [Fact]
    public void UnavailableRuntimeConnectsWhenSteamVrAppears()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var device = new FakeVrOverlayDevice();
        using var supervisor = new VrOverlaySupervisor(
            device,
            new DelegateTimeProvider(() => now));
        supervisor.SetEnabled(true);

        supervisor.Tick(Placement);

        Assert.Equal(VrOverlayStatus.WaitingForSteamVr, supervisor.Status);
        Assert.Equal(0, device.OpenCalls);
        now += VrOverlaySupervisor.AvailabilityPollInterval;
        device.Available = true;

        supervisor.Tick(Placement);

        Assert.Equal(VrOverlayStatus.Connected, supervisor.Status);
        Assert.Equal(1, device.OpenCalls);
        Assert.Equal(1, device.PlacementCalls);
        Assert.Equal(1, device.VisibleCalls);
    }

    [Fact]
    public void QuitClosesAndReconnectsWithoutRestartingFoxTrans()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var device = new FakeVrOverlayDevice { Available = true };
        using var supervisor = new VrOverlaySupervisor(
            device,
            new DelegateTimeProvider(() => now));
        supervisor.SetEnabled(true);
        supervisor.Tick(Placement);
        nint first = device.LastOpened;

        device.ShouldQuit = true;
        supervisor.Tick(Placement);

        Assert.Equal(VrOverlayStatus.WaitingForSteamVr, supervisor.Status);
        Assert.Equal(1, device.CloseCalls);
        Assert.Equal(first, device.LastClosed);
        device.ShouldQuit = false;
        supervisor.Tick(Placement);

        Assert.Equal(VrOverlayStatus.Connected, supervisor.Status);
        Assert.Equal(2, device.OpenCalls);
    }

    [Fact]
    public void FailedOpenUsesTheDeterministicRetrySchedule()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var device = new FakeVrOverlayDevice
        {
            Available = true,
            OpenResult = 42
        };
        using var supervisor = new VrOverlaySupervisor(
            device,
            new DelegateTimeProvider(() => now));
        supervisor.SetEnabled(true);

        supervisor.Tick(Placement);
        Assert.Equal(VrOverlayStatus.Faulted, supervisor.Status);
        Assert.Equal(1, device.OpenCalls);

        foreach (int seconds in new[] { 1, 2, 4, 8, 10 })
        {
            now += TimeSpan.FromSeconds(seconds - 1);
            supervisor.Tick(Placement);
            Assert.Equal(device.OpenCalls, Array.IndexOf(
                new[] { 1, 2, 4, 8, 10 }, seconds) + 1);
            now += TimeSpan.FromSeconds(1);
            supervisor.Tick(Placement);
        }

        Assert.Equal(6, device.OpenCalls);
    }

    [Fact]
    public void SessionClosesExactlyOnce()
    {
        var device = new FakeVrOverlayDevice();
        var session = new VrOverlaySession(device, (nint)7);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, device.CloseCalls);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void DeviceFailuresNeverEscapeTheSupervisor()
    {
        var device = new FakeVrOverlayDevice
        {
            ThrowOnAvailability = true
        };
        using var supervisor = new VrOverlaySupervisor(device);

        supervisor.SetEnabled(true);
        Exception? exception = Record.Exception(() => supervisor.Tick(Placement));

        Assert.Null(exception);
        Assert.Equal(VrOverlayStatus.Faulted, supervisor.Status);
    }

    private sealed class FakeVrOverlayDevice : IVrOverlayDevice
    {
        public bool Available { get; set; }
        public bool ShouldQuit { get; set; }
        public bool ThrowOnAvailability { get; set; }
        public int OpenResult { get; set; }
        public int AvailabilityChecks { get; private set; }
        public int OpenCalls { get; private set; }
        public int PlacementCalls { get; private set; }
        public int VisibleCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public nint LastOpened { get; private set; }
        public nint LastClosed { get; private set; }

        public bool IsAvailable()
        {
            AvailabilityChecks++;
            if (ThrowOnAvailability)
                throw new InvalidOperationException("SteamVR probe failed.");
            return Available;
        }

        public int Open(string key, string name, out nint overlay)
        {
            OpenCalls++;
            overlay = OpenResult == 0 ? (nint)OpenCalls : 0;
            LastOpened = overlay;
            return OpenResult;
        }

        public int Submit(nint overlay, ReadOnlySpan<byte> rgba, uint width, uint height) => 0;

        public int SetPlacement(nint overlay, VrOverlayPlacement placement)
        {
            PlacementCalls++;
            return 0;
        }

        public int SetVisible(nint overlay, bool visible)
        {
            VisibleCalls++;
            return 0;
        }

        public int Poll(nint overlay, out bool shouldQuit)
        {
            shouldQuit = ShouldQuit;
            return 0;
        }

        public void Close(nint overlay)
        {
            CloseCalls++;
            LastClosed = overlay;
        }

        public string DescribeResult(int result) => $"error {result}";
    }
}
