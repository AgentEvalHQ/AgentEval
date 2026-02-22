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

AgentEval is an evaluation and testing toolkit. It does **not** make network calls or access external services on its own. However, the agents you evaluate through AgentEval may call external services such as Azure OpenAI, OpenAI, or other AI providers.

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

AgentEval does **not** collect telemetry, analytics, or usage data. It does **not** phone home or transmit any information. See [PRIVACY.md](PRIVACY.md) for details.

---

*This disclaimer supplements but does not replace the MIT License. In case of conflict, the MIT License governs.*
