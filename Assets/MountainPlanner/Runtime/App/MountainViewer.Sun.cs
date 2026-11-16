using MountainPlanner.Domain.Sky;
using MountainPlanner.Simulation;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.06: while the clock runs the sun moves every frame, drawn between the two newest snapshots
    /// (<see cref="FaceClock"/>), so a fast clock slides the shadows, the sky and the cloud shadows instead of stepping them.
    /// </summary>
    public sealed partial class MountainViewer
    {
        FaceClock _sunClock = new FaceClock();

        void PushSunEveryFrame(ViewTime now)
        {
            if (!now.IsValid) return;
            Lighting.SetTime(now, _sunClock.SecondsPastSnapshot);
            _sunPushed = now;
        }
    }
}
