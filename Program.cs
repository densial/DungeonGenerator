

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

using var generationHttpClient = new HttpClient
{
    Timeout = Timeout.InfiniteTimeSpan
};

var uri = new Uri("http://127.0.0.1:1234/v1/");

var clientOptions = new OpenAIClientOptions
{
    Endpoint = uri,
    Transport = new HttpClientPipelineTransport(generationHttpClient),
    NetworkTimeout = Timeout.InfiniteTimeSpan
};

var chatClient = new OpenAIClient(
        // The OpenAI SDK requires a credential object, but LM Studio ignores this
        // placeholder when its local server authentication is disabled.
        new ApiKeyCredential("not-required"),
        clientOptions)
    .GetChatClient("unsloth/muse-glimmer-30b");



AIAgent agent = chatClient.AsAIAgent(
    name: "DungeonMaster",
    tools:
    [
        AIFunctionFactory.Create(Tools.ListFilesAsync),
        AIFunctionFactory.Create(Tools.ReadFileAsync),
        AIFunctionFactory.Create(Tools.WriteFileAsync),
        AIFunctionFactory.Create(Tools.ReadImageAsync),
        AIFunctionFactory.Create(Tools.ReadImageRegionAsync),
        AIFunctionFactory.Create(Tools.SearchWebAsync),
        AIFunctionFactory.Create(Tools.RunPromptAsync)
    ],
    instructions: """
                  You are an expert tabletop RPG adventure designer specialising in turning dungeon and encounter maps into complete, playable one-shot adventures.

                  Use RunPromptAsync to delegate a bounded task to a fresh independent agent session, including all context the child session needs. Do not recursively delegate unless explicitly required.
                  
                  All input and output files are in the Workspace directory under the solution root. Use that directory for all file discovery, reading, and writing.
                  
                  Your job is to analyse a supplied map image, understand its visible layout and features, and design an adventure that feels specifically created for that location.
                  
                  Treat the map as authoritative for physical layout. Do not invent rooms, doors, passages, exits, or connections that contradict what is visible. Where details are unclear, make reasonable creative interpretations while preserving the map's structure.
                  
                  Design coherent locations rather than simply placing encounters into rooms. Consider why the location exists, who occupies it, what happened there, what the inhabitants want, and how the environment supports the story.
                  
                  Create varied gameplay including exploration, roleplaying, discovery, hazards, puzzles, combat, secrets, and meaningful player choices where appropriate.
                  
                  Rooms, NPCs, encounters, clues, treasure, and environmental features should relate logically to one another and to the overall adventure.
                  
                  Treat the dungeon as a connected, living environment. Consider how inhabitants react to noise, intrusion, alarms, combat, player actions, and changing circumstances.
                  
                  Prefer multiple possible approaches over mandatory combat or single-solution challenges.
                  
                  Design for practical use at the table. Provide the GM with everything necessary to understand and run the adventure without needing to invent essential missing details.
                  
                  Follow the RPG system, party level, adventure length, tone, difficulty, and other constraints supplied in the task prompt.
                  
                  When system-specific rules accuracy matters, use the available web search tool to verify rules from authoritative sources. For D&D 5e, use the official SRD specified by the task as the primary rules source. Do not rely on unofficial rules sites when an official SRD source is available.
                  
                  Maintain rules accuracy where system-specific mechanics are used. Clearly distinguish custom mechanics or creatures from standard game material.
                  
                  Above all, use the architecture and visual features of the supplied map to inspire the adventure. The finished result should feel like this adventure belongs to this map and could not simply have been written for any generic dungeon.
                  
                  Use `RunPromptAsync` whenever a substantial piece of work can be delegated to a fresh model session to reduce context usage or improve focus.
                  
                  Give each delegated prompt a clear objective and enough information for the sub-session to work autonomously. The sub-session may inspect the workspace and use available tools as needed.
                  
                  Prefer delegating large analysis, planning, rules research, validation, or drafting tasks rather than carrying all intermediate work in the main agent context.
                  
                  Keep the main agent focused on orchestration, reviewing results, resolving inconsistencies, and producing the final completed output.
                  """);  

Tools.SetAgent(agent);

var session = await agent.CreateSessionAsync();

var artifactPrompt = """
                      
                     Inspect the files available in your workspace and identify the dungeon or encounter map and any supporting documents.
                     
                     Create a complete, playable D&D 5e one-shot adventure based specifically on the supplied map.
                     
                     Use the map as the authoritative physical layout. Analyse its rooms, passages, entrances, exits, terrain, objects, and other visible features and build the adventure around them.
                     
                     Use any relevant supporting files in the workspace.
                     
                     When you need to verify D&D 5e rules, monsters, spells, conditions, equipment, or other rules content, use the available web search tool and prefer the official SRD 5.2.1 as the authoritative source.
                     
                     Parameters:
                     
                     - System: D&D 5e (2024)
                     - Rules: SRD 5.2.1
                     - Party: 4 characters
                     - Character level: 5
                     - Session length: approximately 4 hours
                     - Difficulty: moderate, with a challenging climax
                     
                     Work autonomously. Decide what files need to be inspected, what rules information needs to be researched, and how best to turn the map into a coherent adventure.
                     
                     The adventure should include a strong premise, background, hook, meaningful exploration, varied encounters, NPCs or creatures where appropriate, clues, hazards, treasure, a climax, and a resolution.
                     
                     Every significant area of the map should have a purpose, but not every room needs combat.
                     
                     Create the finished adventure as:
                     
                     `ADVENTURE.md`
                     
                     First inspect the workspace using the available file-listing tools. Do not call ReadImage until you have identified the actual image file.
                     
                     It must be complete enough for a GM to run without having to invent essential content.
                     
                     Before finishing, review your work for consistency with the map, rules accuracy, encounter variety, pacing, and completeness.
                     
                     Write the final file and report when the task is complete.
                     """;

try
{
    Console.WriteLine("[Program] Starting primary artifact prompt");
    var response = await agent.RunAsync(artifactPrompt, session);
    Console.WriteLine($"[Program] Primary artifact prompt completed: response={response.Text?.Length ?? 0} characters");
    Console.WriteLine(response.Text);
}
catch (OperationCanceledException exception)
{
    Console.Error.WriteLine("[Program][CANCELLED] Primary artifact prompt was cancelled");
    Console.Error.WriteLine(Tools.FormatExceptionDetails(exception));
    throw;
}
catch (Exception exception)
{
    Console.Error.WriteLine("[Program][ERROR] Primary artifact prompt failed");
    Console.Error.WriteLine(Tools.FormatExceptionDetails(exception));
    throw;
}
