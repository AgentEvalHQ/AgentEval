# Connect to a Microsoft 365 tenant

This page sets up a Microsoft 365 tenant so that AgentEval can reach its agents over Work IQ, and checks the
connection before any prompt is sent. It is the one place for these steps: the [demo](../samples/AgentEval.Microsoft365Demo/README.md),
the [tester guide](microsoft365-tester-guide.md) and the [Microsoft 365 page](microsoft365.md) link here.

Plan about 30 minutes, plus up to 30 minutes for a billing-plan assignment to take effect. The steps follow Microsoft's
[Work IQ setup](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/enable-work-iq) and
[A2A quickstart](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/a2a/quickstart). If
Microsoft's pages and this one disagree, Microsoft's pages are right: tell us.

## Before you start

- **Use a non-production tenant and a test user.** The permission covers reads **and writes** of the user's mail,
  files, meetings and chats. AgentEval never approves an action or resends a prompt, but the agent acts as that user.
- **Work IQ calls cost money.** They are billed as Copilot Credits through a usage-based billing plan, separately from
  any Microsoft 365 subscription. Agree the budget with the tenant owner first.
- **Who does what.** A Global Administrator enables Work IQ once (step 1). A billing administrator sets up the plan
  (step 2). Anyone who may register apps does steps 3 to 5. An administrator who can grant tenant-wide consent grants
  it (step 5). The tester does steps 7 and 8.

## 1. Enable Work IQ in the tenant (once)

A **Global Administrator** creates the service principal of the Work IQ API in the tenant. With the Azure CLI:

```bash
az login --tenant <tenant id>
az ad sp create --id fdcc1f02-fc51-4226-8753-f668596af7f7
```

Without the CLI, use [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer): `POST`
`https://graph.microsoft.com/v1.0/servicePrincipals` with the body `{"appId": "fdcc1f02-fc51-4226-8753-f668596af7f7"}`.
*201 Created* means it worked. An error that the service principal already exists means Work IQ was already enabled.

## 2. Set up billing for the test user

In Copilot Studio, the tenant needs a **usage-based billing plan** with an Azure subscription and resource group
assigned, and the **test user must be assigned to the plan**. Allow 15 to 30 minutes after the assignment. Until then,
Work IQ answers `403 Forbidden` with no scope named.

## 3. Register the app

In the [Microsoft Entra admin center](https://entra.microsoft.com): *Entra ID* > *App registrations* >
*New registration*.

- *Name*: any, for example `AgentEval Work IQ connector`.
- *Supported account types*: **Accounts in this organizational directory only**.
- No redirect URI is needed. Select *Register*.

On the app's *Overview* page, copy the **Application (client) ID** and the **Directory (tenant) ID**.

## 4. Make it a public client

On the app: *Authentication* > *Advanced settings* > **Allow public client flows** = **Yes** > *Save*.

AgentEval signs in with a device code and holds no secret. Microsoft's own samples sign in with Windows (WAM) or a
browser. If you also want to run them, add the *Mobile and desktop applications* platform and the redirect URIs the
quickstart lists. AgentEval does not need them.

## 5. Add the permission and grant consent

On the app: *API permissions* > *Add a permission* > *APIs my organization uses*. Search for **Work IQ**, choose
*Delegated permissions*, select **`WorkIQAgent.Ask`** and choose *Add permissions*.

Then an administrator selects **Grant admin consent for** *<your tenant>* and confirms. The permission requires admin
consent: without it, sign-in fails with `AADSTS65001`.

## 6. Check that device-code sign-in is allowed

Many organisations block device-code sign-in with a Conditional Access policy. If the tenant does, sign-in fails with
`AADSTS53003`. Ask an administrator to exclude the test user, or this app, from that policy for the test, or use a test
tenant without it. AgentEval has no other sign-in method yet.

## 7. Write the config file

From the repository root:

```bash
cp samples/datasets/m365-config.template.json workiq.json
```

Put the *Directory (tenant) ID* in `tenantId` and the *Application (client) ID* in `appClientId`. Set `timeZone` to
your IANA time zone, such as `Europe/Zurich`. Leave `agentId` as `null` to talk to Microsoft 365 Copilot itself, or set
the id of a declarative agent the test user can reach (ids look like `U_abc.def`). The file holds no secret, and Git
ignores `workiq.json`. Every field is described in [Authoring the config JSON](microsoft365.md#authoring-the-config-json).

## 8. Test the connection

Run the readiness check. It sends no prompt.

```bash
# The demo
dotnet run --project samples/AgentEval.Microsoft365Demo -- --readiness-only

# Or the CLI
agenteval m365 doctor --m365-config workiq.json
```

The first time, a message asks you to open `https://microsoft.com/devicelogin` and enter a code. Do it on any device and
sign in **as the test user**. The token is cached outside the repository, so later runs sign in silently.

The check passes when `config`, `auth`, `discovery` and `protocol` all PASS and the command exits `0`. That proves the
config is valid, the test user can sign in, Work IQ serves the agent's card, and the service speaks A2A 1.0. What each
stage checks is in [Readiness check](microsoft365.md#readiness-check-agenteval-m365-doctor).

## If a step fails

| What you see | Cause | Fix |
|---|---|---|
| `AADSTS7000218` or `AADSTS7000215` | Public client flows are off. | Step 4. |
| `AADSTS65001` | Admin consent for `WorkIQAgent.Ask` is missing. | Step 5. |
| `AADSTS650052` | Work IQ is not enabled in the tenant. | Step 1, then step 5. |
| `AADSTS53003` | A Conditional Access policy blocked the sign-in. | Step 6. |
| `AADSTS90002` or `AADSTS700016` | `tenantId` or `appClientId` is wrong. | Step 7: copy both again from the app's *Overview* page. |
| HTTP `401` | The token is not for Work IQ. | Check that `scope` in `workiq.json` is `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`. |
| HTTP `403` that names the required scopes | Admin consent is missing. | Step 5. |
| HTTP `403` with no scope named | The test user is not in the billing plan, or was added less than 30 minutes ago. | Step 2. |

Every other message is in the [troubleshooting catalogue](microsoft365.md#troubleshooting).

## When you are done

Delete the app registration: that also removes its consent. Remove the test user from the billing plan if nobody else
needs it. Delete `workiq.json`, and the sign-in token cache: the `AgentEval/msal_cache` folder in your local
application data (`%LOCALAPPDATA%` on Windows).
