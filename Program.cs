using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var commandLine = AdventureCommandLine.Parse(args);
if (commandLine.ShowHelp)
{
    Console.WriteLine(AdventureCommandLine.Usage);
    return;
}

if (commandLine.Error is not null)
{
    Console.Error.WriteLine($"[Program][ERROR] {commandLine.Error}");
    Console.Error.WriteLine(AdventureCommandLine.Usage);
    Environment.ExitCode = 2;
    return;
}

try
{
    ToolWorkspace.ConfigureAdventure(commandLine.AdventureDirectory!);
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"[Program][ERROR] Invalid adventure directory: {exception.Message}");
    Console.Error.WriteLine(AdventureCommandLine.Usage);
    Environment.ExitCode = 2;
    return;
}

using var applicationCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    if (applicationCancellation.IsCancellationRequested)
    {
        eventArgs.Cancel = false;
        return;
    }

    eventArgs.Cancel = true;
    Console.Error.WriteLine("[Program] Cancellation requested; preserving the latest checkpoint. Press Ctrl+C again to terminate immediately.");
    applicationCancellation.Cancel();
};
var cancellationToken = applicationCancellation.Token;
var adventureDirectory = ToolWorkspace.AdventureRelativePath;
var artifactPath = ToolWorkspace.ArtifactRelativePath;

var settings = await GeneratorSettings.LoadAsync("appsettings.json", cancellationToken);

IList<AITool> agentTools =
[
    AIFunctionFactory.Create(ListFilesTool.ListFilesAsync),
    AIFunctionFactory.Create(ReadFileTool.ReadFileAsync),
    AIFunctionFactory.Create(WriteFileTool.WriteFileAsync),
    AIFunctionFactory.Create(AppendFileTool.AppendFileAsync),
    AIFunctionFactory.Create(ReadImageTool.ReadImageAsync),
    AIFunctionFactory.Create(ReadImageRegionTool.ReadImageRegionAsync),
    AIFunctionFactory.Create(SearchWebTool.SearchWebAsync),
    AIFunctionFactory.Create(RunPromptTool.RunPromptAsync)
];

Console.WriteLine($"[Program] Registered {agentTools.Count} tools: {string.Join(", ", agentTools.Select(tool => tool.Name))}");

var agentInstructions = $$"""
                  You are an expert tabletop RPG adventure designer specialising in turning dungeon and encounter maps into complete, playable one-shot adventures.

                  Use RunPromptAsync only for a bounded task that genuinely benefits from an independent session. The primary session must complete source discovery, template inspection, map analysis, and the first `{{artifactPath}}` checkpoint before delegating. Include all relevant findings in the child prompt so the child does not reread shared source files. Never recursively delegate.
                  
                  Shared input files are in the root of the Workspace directory. Files specific to this adventure are in `{{adventureDirectory}}`. The file tools expose only those shared root files and this selected adventure directory. Do not attempt to access another adventure directory.

                  Write every adventure-specific file inside `{{adventureDirectory}}`. The tools enforce this boundary. The final artifact path for this run is `{{artifactPath}}`.
                  
                  Your job is to analyse a supplied map image, understand its visible layout and features, and design an adventure that feels specifically created for that location.

                  Keep the operator informed throughout the run. Progress reporting is part of the task, not just part of the final response.

                  - Begin with a concise plan that identifies the immediate objective and likely tool calls.
                  - Before every tool call, output a short `[Status]` line naming the tool, what you are about to do, and why it is needed.
                  - Supply the same concise explanation in the tool's `reason` argument.
                  - After every tool result, output a short `[Result]` line summarising what was learned or changed and what you will do next.
                  - Announce important decisions, changes of approach, delegation, retries, validation passes, file writes, and task completion.
                  - For long drafting or analysis phases, provide occasional checkpoint updates rather than remaining silent.
                  - If a tool or model call fails, report the failure, its practical impact, and the recovery action before retrying or choosing an alternative.
                  - Keep updates factual and concise. Explain operational rationale, but do not reveal private chain-of-thought or hidden reasoning.
                  - Do not include these status messages inside `{{artifactPath}}`; they belong only in the live run output.

                  Build the output document incrementally instead of waiting until the whole adventure is drafted.

                  - Find and read the adventure-generation template before drafting.
                  - The primary session owns source acquisition: list the workspace once, read the template once, and load the full map once. Use ReadImageRegionAsync only when a specific map detail needs closer inspection.
                  - Use the template's section order and coverage requirements, but write real finished content only. Never copy template comments, instructions, or unresolved placeholders into the output.
                  - After the initial file discovery, template review, and map analysis, create `{{artifactPath}}` immediately with the title, metadata, and the first completed sections.
                  - Create that first artifact checkpoint before any RunPromptAsync call. Delegation is for later bounded drafting, rules verification, or review—not for repeating startup discovery.
                  - As each subsequent section becomes complete and internally consistent, append it to `{{artifactPath}}` with `AppendFileAsync`.
                  - Append coherent Markdown sections in final reading order. Do not append scratch notes, incomplete prose, status messages, or speculative alternatives.
                  - Do not hold the entire adventure until the end and then perform one large write. Prefer regular durable checkpoints after each major section or closely related group of sections.
                  - Before appending, verify that headings, numbering, and transitions follow the template and the content does not duplicate material already written.
                  - Near completion, read the assembled `{{artifactPath}}`, validate it against the template and map, and correct any inconsistencies. Use `WriteFileAsync` for a full replacement only when a final structural correction cannot be made safely by appending.
                  - The finished artifact must be the Markdown file `{{artifactPath}}`.
                  
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
                  
                  Use `RunPromptAsync` selectively after the first artifact checkpoint, with no more than four delegated sessions in a run. Do not delegate work that the primary session can complete directly from context it already has.
                  
                  Give each delegated prompt a clear objective and a source brief containing all relevant template structure, map findings, adventure decisions, and constraints. A delegated session must not reread `ADVENTURE_GENERATION_TEMPLATE.md` or reload the full map unless its prompt identifies a specific missing fact that cannot be supplied in the source brief.
                  
                  If you are already running inside a delegated session, do not call RunPromptAsync. Return the bounded result to the primary session. Do not write or append `{{artifactPath}}` from a delegated session unless the delegated prompt explicitly assigns ownership of a named section.
                  
                  Keep the main agent focused on orchestration, reviewing results, resolving inconsistencies, and producing the final completed output.
                  """;

RunStateManager? runState = null;
try
{
    runState = await RunStateManager.BeginAsync(cancellationToken);
    await using var generator = await AgentFactory.CreateAsync(
        settings,
        name: "DungeonMaster",
        instructions: agentInstructions,
        tools: agentTools,
        cancellationToken);
    AIAgent agent = generator.Agent;

    RunPromptTool.SetAgent(agent);
    var session = await agent.CreateSessionAsync(cancellationToken);

    var artifactPrompt = $$"""
                         Inspect the files available in your workspace and identify the dungeon or encounter map and any supporting documents.

                         The selected adventure directory is `{{adventureDirectory}}`. Treat root-level Workspace files as shared inputs and files beneath `{{adventureDirectory}}` as specific to this adventure. Do not use files from any other adventure directory.

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

                         `{{artifactPath}}`

                         Use the shared root file `ADVENTURE_GENERATION_TEMPLATE.md` as the structural template. Read the template and the adventure-specific full map once in this primary session. Once you have established a coherent premise, create `{{artifactPath}}` early with finished opening sections and before making any RunPromptAsync call. Continue by appending completed Markdown sections throughout the run rather than waiting to write the entire document at the end.

                         First inspect the workspace using the available file-listing tools. Do not call ReadImage until you have identified the actual image file.

                         It must be complete enough for a GM to run without having to invent essential content.

                         Before finishing, review your work for consistency with the map, rules accuracy, encounter variety, pacing, and completeness.

                         Write the final file and report when the task is complete.
                         """;
    artifactPrompt += $"{Environment.NewLine}{Environment.NewLine}{runState.RecoveryInstructions}";

    Console.WriteLine("[Program] Starting primary artifact prompt");
    var response = await AgentRunner.RunAsync(
        agent,
        artifactPrompt,
        session,
        cancellationToken,
        runLabel: "primary-artifact");
    await runState.MarkCompletedAsync(response, cancellationToken);
    Console.WriteLine($"[Program] Primary artifact prompt completed: response={response.Length} characters");
}
catch (OperationCanceledException exception)
{
    if (runState is not null)
    {
        await runState.MarkCancelledAsync(exception);
    }

    Console.Error.WriteLine("[Program][CANCELLED] Shutdown complete. The latest valid checkpoint was preserved for the next run.");
    Environment.ExitCode = 130;
}
catch (Exception exception)
{
    if (runState is not null)
    {
        await runState.MarkFailedAsync(exception);
    }

    Console.Error.WriteLine("[Program][ERROR] Primary artifact prompt failed");
    Console.Error.WriteLine(ExceptionFormatter.FormatDetails(exception));
    throw;
}
