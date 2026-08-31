//
// SWPChipEventController.cs — wires an SWPController to the existing
// ChipEventController stimulus framework.
//
// The ChipEventController already owns the shape of this problem: an opcode
// arrives over TCP, becomes an event on the emulation thread, and the client's
// acknowledgement is held back until the chip has finished reacting, including
// any virtual-time delays it scheduled. SWP activation *is* a virtual-time
// delay — P5 for the line to settle, then up to P6/P7 waiting for the slave —
// so the two fit together with no new machinery:
//
//   PowerUpSWP   (opcode 0x07) -> ACTIVATE   the link
//   PowerDownSWP (opcode 0x08) -> DEACTIVATE the link
//
// The one piece of glue that matters is SWPTimer.Scheduler. Left alone, the
// controller posts its P-delays straight onto the clock source, and the
// ChipEventController would acknowledge 0x07 the instant Activate() returned —
// long before the link was actually up. Pointing the controller's timer at the
// base class's DelayedSequencer instead puts every SWP delay on the same queue
// the acknowledgement handshake watches, so the client's reply arrives only
// once the link has genuinely reached ACTIVATED (or the activation has failed).
//
// This class is chip-agnostic; SupportsInterface reports SWP only. A real chip
// with more interfaces should subclass it, or copy the four lines of glue in
// the constructor into its own ChipEventControllerBase subclass.
//
using System;
using System.Text;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SWP;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SWP
{
    /// <summary>
    /// Drives an <see cref="SWPController"/> from ChipEventController opcodes.
    ///
    ///     swp:       SWP.SWPController @ sysbus
    ///         port: 3460
    ///     uicc:      SWP.SWPSlave @ swp
    ///     chipEvents: SWP.SWPChipEventController @ sysbus
    ///         controller: swp
    ///         controlPort: 3456
    ///
    /// Then `0x07` on the event port activates the link and `0x08` deactivates
    /// it, each acknowledged only once the transition has completed.
    /// </summary>
    public class SWPChipEventController : ChipEventControllerBase
    {
        public SWPChipEventController(IMachine machine, SWPController controller, int controlPort = 3456,
            int sessionPortBase = 0, int maxSessions = 16, int unclaimedSessionTimeout = 60,
            bool autoStart = true, int completionTimeout = 5)
            : base(machine, controlPort, sessionPortBase, maxSessions, unclaimedSessionTimeout, autoStart,
                completionTimeout)
        {
            if(controller == null)
            {
                throw new ConstructionException("SWPChipEventController needs a controller to drive");
            }
            this.controller = controller;

            // The glue. Every P1..P7 delay the controller schedules now lands on
            // the base class's sequencer, which is what the acknowledgement
            // handshake watches - so a client's 0x07 is answered when the link
            // is up, not when Activate() returned.
            controller.Timer.Scheduler = (delay, action) => Sequencer.Post(DelayedStep.After(delay, action));

            controller.StateChanged += OnLinkStateChanged;
            controller.ActivationFailed += OnActivationFailed;
        }

        /// <summary>This part has an SWP interface and nothing else.</summary>
        public override bool SupportsInterface(ChipInterface iface) => iface == ChipInterface.SWP;

        /// <summary>Opcode 0x07. Acknowledged once the link reaches ACTIVATED or the activation fails.</summary>
        public override void PowerUpSWP()
        {
            this.Log(LogLevel.Debug, "PowerUpSWP: activating the SWP link");
            lastActivationFailed = false;
            controller.Activate();
        }

        /// <summary>Opcode 0x08. Acknowledged once the link has been held low for P4 and is DEACTIVATED.</summary>
        public override void PowerDownSWP()
        {
            this.Log(LogLevel.Debug, "PowerDownSWP: deactivating the SWP link");
            controller.Deactivate();
        }

        // --- extra Monitor verbs, beyond the two power opcodes ------------------

        /// <summary>`sysbus.chipEvents SuspendLink`</summary>
        public string SuspendLink()
        {
            Machine.HandleTimeDomainEvent<object>(_ => controller.Suspend(), null, false);
            return controller.State.ToString();
        }

        /// <summary>`sysbus.chipEvents ResumeLink`</summary>
        public string ResumeLink()
        {
            Machine.HandleTimeDomainEvent<object>(_ => controller.Resume(), null, false);
            return controller.State.ToString();
        }

        /// <summary>`sysbus.chipEvents LinkState`</summary>
        public string LinkState() => controller.State.ToString();

        /// <summary>True if the most recent ACTIVATE gave up on P6/P7 rather than coming up.</summary>
        public bool LastActivationFailed => lastActivationFailed;

        public override void Reset()
        {
            base.Reset();
            lastActivationFailed = false;
        }

        public override void Dispose()
        {
            controller.StateChanged -= OnLinkStateChanged;
            controller.ActivationFailed -= OnActivationFailed;
            controller.Timer.Scheduler = null;
            base.Dispose();
        }

        private void OnLinkStateChanged(SWPStateChangedEventArgs args)
        {
            this.Log(LogLevel.Debug, "SWP link {0}", args);
        }

        private void OnActivationFailed()
        {
            lastActivationFailed = true;
            this.Log(LogLevel.Warning, "SWP activation failed; the link stays DEACTIVATED");
        }

        private readonly SWPController controller;
        private volatile bool lastActivationFailed;
    }
}
