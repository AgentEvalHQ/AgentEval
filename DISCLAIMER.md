# Disclaimer

## Preview / Experimental Status

AgentEval is **preview software (work in progress)**. It is under active development and:

- APIs, interfaces, and behavior may change without notice between versions.
- It may contain defects, inaccuracies, or security vulnerabilities.
- It has not been validated for production, mission-critical, or safety-critical use.

**Do not use AgentEval in production or safety-critical systems without independent review, testing, and hardening.**

## AI-Assisted Development

Portions of AgentEval's source code, tests, documentation, and samples were created with assistance from AI tools (including large language models). All AI-generated content has been reviewed by human maintainers before inclusion.

Despite review:
- **Errors may exist.** AI tools can produce plausible but incorrect code, assertions, or documentation.
- **You are responsible** for validating the correctness, security, and compliance of any output for your specific use case.
- **No guarantee** is made that AI-assisted content is free of intellectual property issues, though reasonable efforts have been made to ensure originality and license compliance.

## External Services and APIs

AgentEval is an evaluation and testing toolkit. It contacts external services only when you use a feature that needs them:

- **Model calls.** Evaluators, judges, red-team attacks and Gatekeeper call the model clients you pass in. The CLI builds model clients from your flags and environment variables, and can pick up a provider key already set for another tool (see [PRIVACY.md](PRIVACY.md#provider-auto-detection)).
- **Opt-in network features.** Benchmark pack downloads, the live package-registry check, the decision-model judge and the Copilot Studio connector each contact a remote service once you turn them on. [PRIVACY.md](PRIVACY.md) lists each one, what it contacts and what it sends.

The agents you evaluate through AgentEval may also call external services such as Azure OpenAI, OpenAI, or other AI providers. Those calls are made by your code.

When using AgentEval with external AI services:
- **You are responsible** for your own API keys, credentials, costs, and data sent to external services.
- External services are subject to **their own terms of service**, privacy policies, and usage policies.
- AI model outputs may be **wrong, incomplete, biased, or harmful** — you must validate all outputs independently.
- AgentEval does not control, endorse, or guarantee the behavior of any external AI service.

## No Professional Advice

AgentEval provides evaluation metrics, test assertions, and analytical outputs. These are **not** professional advice of any kind (legal, security, medical, financial, or otherwise). Do not rely on AgentEval's outputs as the sole basis for decisions in regulated, safety-critical, or high-stakes environments.

## Human Responsibility

If AgentEval generates recommendations, scores, or analysis, a qualified human must review and validate those outputs before acting on them. AgentEval is a tool to assist human judgment, not replace it.

## License and Warranty

This project is licensed under the **MIT License**. As stated in the [LICENSE](LICENSE) file:

> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Data and Privacy

AgentEval has **no** telemetry, analytics, crash reporting or update check, and it does not send usage data to its authors or to anyone else. It opens network connections only for the features listed in [PRIVACY.md](PRIVACY.md), each of which stays off until you turn it on. Results, transcripts and reports are written to local files and are not uploaded. See [PRIVACY.md](PRIVACY.md) for details, including the browser-side requests made by the memory benchmark HTML report and the Mission Control GraphQL IDE.

---

*This disclaimer supplements but does not replace the MIT License. In case of conflict, the MIT License governs.*
