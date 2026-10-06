---
applyTo: "samples/**/*.cs"
description: Guidelines for creating AgentEval samples
---

# Sample Implementation Guidelines

## Sample Naming Convention
Samples are numbered: `Sample01_HelloWorld`, `Sample02_AgentWithOneTool`, etc.

## Sample Structure
Each sample should:
1. Have a clear single focus
2. Include time-to-understand estimate
3. Run against the configured model (`AIConfig`). With no provider, stop and say how to configure one. Never switch
   to a mock or scripted stand-in on your own: a mock runs only when the user asks (`--mock`), and every result it
   prints is labelled MOCK. Through 0.42 several samples switched silently and printed ✅ PASSED over canned replies.
4. Print clear step-by-step output

## Sample Header Template
```csharp
/// <summary>
/// Sample XX: Title - Short description
/// 
/// This demonstrates:
/// - Feature 1
/// - Feature 2
/// - Feature 3
/// 
/// ⏱️ Time to understand: X minutes
/// </summary>
public static class Sample_XX_Title
{
    public static async Task RunAsync()
    {
        PrintHeader();
        
        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   ⚠️  This sample needs a model provider.\n");
            return;
        }
        
        // Step 1, 2, 3...
    }
}
```

## Console Output Pattern
Use consistent formatting:
```csharp
Console.WriteLine("📝 Step 1: Creating agent...\n");
Console.WriteLine($"   ✓ Agent '{agent.Name}' created\n");

Console.WriteLine("📊 RESULTS:");
Console.WriteLine(new string('─', 60));
```

## Using AIConfig
All samples should use `AIConfig` class for configuration. It resolves the provider `AI_INFERENCE_PROVIDER` selects
(bitdeer, openai, foundry, azure, openai-compatible):
```csharp
var chatClient = AIConfig.CreateChatClient(AIConfig.ModelDeployment);
```

## Mock Agents (only on request)
A mock is an offline walkthrough the user asks for (`--mock`, read as `AIConfig.UseMock`). It is never a fallback for
missing credentials. Gate the sample with `AIConfig.StartModelSample()` (it prints the MOCK banner, or stops when there
is neither a provider nor `--mock`), name the mock agent "(Mock)", and append `AIConfig.MockLabel` to every pass/fail
line:
```csharp
if (!AIConfig.StartModelSample())
    return;
var agent = AIConfig.UseMock ? CreateMockAgent() : CreateRealAgent();

private static AIAgent CreateMockAgent()
{
    var mockClient = new MockChatClient("Expected response text");
    return new ChatClientAgent(mockClient, new ChatClientAgentOptions
    {
        Name = "MockAgent",
        ChatOptions = new() { Instructions = "You are a helpful assistant." }
    });
}
```

## Registering in Program.cs
Add sample to the menu in `samples/AgentEval.Samples/Program.cs`:
```csharp
("XX. Sample Title", Sample_XX_Title.RunAsync),
```

## Key Takeaways Section
End each sample with:
```csharp
Console.WriteLine("\n💡 KEY TAKEAWAYS:");
Console.WriteLine("   • Key point 1");
Console.WriteLine("   • Key point 2");
Console.WriteLine("\n🔗 NEXT: Run Sample XX+1 to see feature Y!\n");
```
