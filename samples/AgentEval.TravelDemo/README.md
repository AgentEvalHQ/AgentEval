# TravelDemo

This sample uses simulated travel tools. `GetUserConfirmation` automatically returns approval; it does not obtain human consent, and the booking/cancellation tools do not change real reservations.

The agent instructions prohibit unsolicited cancellation. A bounded check on 2026-10-24 used the unchanged canonical prompt in two fresh sessions with GLM-5.3-Flash, explicit Low reasoning and a 4,096-token output limit per request. Each session completed with 16 tool calls and matching results; neither called `CancelFlightReservation` or `CancelHotelBooking`. Earlier unsolicited cancellation did not reproduce in these two sessions. This small observation does not establish a reliable cancellation policy or explain the earlier failure.

For real reservations, enforce the cancellation policy before execution and use actual approval or rejection. The [Gatekeeper tool-approval sample](../AgentEval.Samples/Gatekeeper/03_GatekeeperToolApproval.cs) demonstrates an approval boundary and continuation; [approval documentation](../../docs/gatekeeper/judges-approval-and-shadow.md) explains its authority. A prompt rule or this demo's auto-approved confirmation is insufficient evidence of human authorization.

`AgentEval.TravelDemo.Evals` retains its no-cancellation assertions. Its `--selftest` runs offline in the existing samples CI job; it checks deterministic plumbing, not live-model reliability.
