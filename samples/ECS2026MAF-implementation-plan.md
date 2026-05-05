# ECS2026MAF — Implementation Plan

> Conference demo project for ECS 2026: pure MAF code with AgentEval evaluation decoupled into a sibling project.
> Neither project is added to `AgentEval.sln`.

---

## Goals

1. **ECS2026MAF** — Zero AgentEval dependencies. Shows MAF agents and workflows in the cleanest possible way.
2. **ECS2026MAF.Evals** — Adds AgentEval assertions / metrics as a separate concern, referencing ECS2026MAF.

---

## Folder Structure

```
samples/
├── ECS2026MAF/
│   ├── ECS2026MAF.csproj
│   ├── Config.cs                         # Azure OpenAI config (env-var based)
│   ├── Program.cs                        # Entry: numbered menu → run demo
│   │
│   ├── Tools/
│   │   └── TravelTools.cs                # UNIFIED tools reused by both demos
│   │                                     # SearchFlights(from,to,date), BookFlight,
│   │                                     # BookHotel, GetInfoAbout, SendConfirmation,
│   │                                     # GetUserConfirmation, CancelBooking
│   │
│   ├── Agents/                           # One file per agent (Demo 2)
│   │   ├── TravelAgentFactory.cs         # Creates TravelBookingAgent (Demo 1)
│   │   ├── TripPlannerAgentFactory.cs    # Creates TripPlanner agent
│   │   ├── FlightReservationAgentFactory.cs
│   │   ├── HotelReservationAgentFactory.cs
│   │   └── PresenterAgentFactory.cs
│   │
│   ├── Workflows/
│   │   └── TripPlannerWorkflow.cs        # Assembles 4-agent sequential workflow
│   │
│   └── Demos/
│       ├── Demo01_TravelAgent.cs         # TravelAgent: create → run → print output
│       ├── Demo02_TripPlannerWorkflow.cs # TripPlannerWorkflow: create → run → print output
│       └── Demo03_LiveDemo.cs            # Placeholder (to be built live at ECS 2026)
│
└── ECS2026MAF.Evals/
    ├── ECS2026MAF.Evals.csproj           # ProjectReference to ECS2026MAF + AgentEval NuGet
    ├── Program.cs                        # Entry: numbered eval menu
    └── Evals/
        ├── Eval01_TravelAgentEvals.cs    # AgentEval behavioral policy assertions
        └── Eval02_TripPlannerEvals.cs    # AgentEval workflow assertions
```

---

## Tools Unification Strategy

Both demos share a single `Tools/TravelTools.cs`.

| Tool | Demo 1 (TravelAgent) | Demo 2 (TripPlanner) |
|---|---|---|
| `SearchFlights(fromCity, toCity, date)` | ✓ (fromCity="" for single-city) | ✓ |
| `BookFlight(flightNumber, passengers)` | ✓ | ✓ |
| `SendConfirmation(email)` | ✓ | — |
| `GetUserConfirmation(action)` | ✓ | — |
| `CancelBooking(bookingRef)` | ✓ | — |
| `GetInfoAbout(city)` | — | ✓ |
| `BookHotel(city, checkIn, checkOut, guests)` | — | ✓ |

The `SearchFlights` signature is unified to `(fromCity, toCity, date)`. The TravelAgent system prompt
asks it to search flights "from [origin]" so it naturally provides both cities.

---

## Project References

### ECS2026MAF.csproj
```xml
<PackageReference Include="Microsoft.Agents.AI" Version="1.3.0" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.8.0-beta.1" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.5.0" />
```

### ECS2026MAF.Evals.csproj
```xml
<ProjectReference Include="../ECS2026MAF/ECS2026MAF.csproj" />
<PackageReference Include="AgentEval" Version="0.8.1-beta" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.8.0-beta.1" />
<PackageReference Include="Microsoft.Agents.AI" Version="1.3.0" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.0" />
```

---

## Demo Descriptions

### Demo 01 — TravelAgent (ECS2026MAF)
- Source: `AgentEval.NuGetConsumer/AgentFactory.cs` → `CreateTravelAgent(useMock: false)`
- **No AgentEval**: uses raw `AIAgent.InvokeAsync` or `ChatClientAgent`
- Prints: the agent response and any console tool output
- Execution: `Console.WriteLine` per tool call so audience sees it live

### Demo 02 — TripPlannerWorkflow (ECS2026MAF)
- Source: `03_WorkflowWithTools.cs` → `CreateTripPlannerWorkflow()`
- **No AgentEval**: uses `Workflow.RunAsync` directly (or via `WorkflowBuilder`)
- 4 agents: TripPlanner → FlightReservation → HotelReservation → Presenter
- Each agent in its own factory file under `Agents/`
- Tools from shared `Tools/TravelTools.cs`
- Prints: each step's output as it arrives

### Demo 03 — Live Demo Placeholder (ECS2026MAF)
- Empty scaffold: `Task RunAsync()` with a `Console.WriteLine("🚧 To be built live at ECS 2026!")`
- Clean entry point ready to be filled in during the talk

### Eval 01 — TravelAgent Evals (ECS2026MAF.Evals)
- Reuses `TravelAgentFactory.CreateTravelAgent()` from ECS2026MAF
- Adds `MAFEvaluationHarness` + behavioral policy assertions
- Mirrors `Demos.RunBehavioralPoliciesDemo` pattern from NuGetConsumer

### Eval 02 — TripPlanner Evals (ECS2026MAF.Evals)
- Reuses `TripPlannerWorkflow.Create()` from ECS2026MAF
- Adds `WorkflowEvaluationHarness` + workflow assertions
- Mirrors `WorkflowWithTools.RunAsync` pattern from Samples

---

## Key Design Decisions

1. **One class = one file** — every agent factory, demo, and eval lives in its own `.cs` file.
2. **Static classes** — demos and factories use `static class` + `static async Task RunAsync()` for simplicity.
3. **No mocking** — ECS2026MAF targets live Azure OpenAI. The demos skip gracefully if `Config.IsConfigured` is false.
4. **Tools reuse** — `TravelTools.cs` is the single source of truth for all tool implementations. Agents reference it directly via `AIFunctionFactory.Create(TravelTools.SearchFlights)`.
5. **Zero AgentEval in ECS2026MAF** — not even a `using AgentEval;` statement.
6. **ECS2026MAF.Evals is purely additive** — it only adds assertions and metrics; the agent/workflow creation is imported unchanged from ECS2026MAF.

---

## Running

```powershell
# Demo project (no evals)
dotnet run --project samples/ECS2026MAF

# Evals project
dotnet run --project samples/ECS2026MAF.Evals
```

---

## Git Branch

Branch: `feature/ECS2026MAF`
Does NOT modify `AgentEval.sln`.
