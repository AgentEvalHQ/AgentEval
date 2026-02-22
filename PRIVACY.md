# Privacy Statement

## No Telemetry

AgentEval does **not** collect, transmit, or store any telemetry, analytics, usage data, or personally identifiable information.

## No Network Calls

AgentEval itself makes **no network calls**. It is a local evaluation and testing toolkit that observes and measures your AI agents.

Your agents, tested through AgentEval, may call external services (such as Azure OpenAI or OpenAI). Those calls are initiated by **your code and your agents**, not by AgentEval. You are responsible for understanding and complying with the privacy policies and terms of service of any external services your agents use.

## Test Data Responsibility

AgentEval processes test cases, evaluation prompts, and agent responses that **you provide**. You are responsible for ensuring that your test data does not contain sensitive, personal, or regulated information unless you have appropriate controls in place.

## Dependencies

AgentEval depends on Microsoft .NET libraries and third-party packages listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). These dependencies may have their own privacy policies. AgentEval does not configure or enable any telemetry in its dependencies.

Note: AgentEval's CI/CD configuration explicitly disables .NET CLI telemetry (`DOTNET_CLI_TELEMETRY_OPTOUT: true`).

---

*Last updated: February 2026*
