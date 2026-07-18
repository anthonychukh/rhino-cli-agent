using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoAgent.Config;
using RhinoAgent.Memory;
using RhinoAgent.Providers;
using RhinoAgent.Runtime;
using RhinoAgent.Skills;
using RhinoAgent.Tools;

namespace RhinoAgent.Commands;

[Guid("CC864274-DB30-412A-949B-3EEFE9C57141")]
public sealed class AgentSelfTestCommand : Command
{
    private const string SelfTestMarkerKey = "RhinoAgentSelfTest";

    public override string EnglishName => "AgentSelfTest";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var outputPath = GetOutputPath();
        var registeredCommands = Command.GetCommandNames(true, true)
            .Where(name => name.StartsWith("Agent", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name)
            .ToArray();
        var parsed = AgentResponseParser.Parse(
            """
            visible response
            <rhino-agent>{"tool_calls":[{"tool":"document_summary","arguments":{}}]}</rhino-agent>
            """);

        var success = parsed.VisibleText == "visible response"
            && parsed.ToolCalls.Count == 1
            && parsed.ToolCalls[0].Tool == "document_summary";

        var lineRecognized = AgentCommand.TryResolveManualRhinoCommand("Line", out var lineScript, out var lineMatchedBy)
            && lineMatchedBy == "command"
            && lineScript == "Line";
        var prefixedRecognized = AgentCommand.TryResolveManualRhinoCommand("_Circle 0,0,0 5", out var prefixedScript, out var prefixedMatchedBy)
            && prefixedMatchedBy == "prefix"
            && prefixedScript == "_Circle 0,0,0 5";
        var aliasNames = GetAliasNames();
        var aliasProbe = aliasNames.FirstOrDefault();
        var aliasRecognized = aliasProbe is null
            || AgentCommand.TryResolveManualRhinoCommand(aliasProbe, out _, out var aliasMatchedBy) && aliasMatchedBy == "alias";
        success = success && lineRecognized && prefixedRecognized && aliasRecognized;

        var scriptedToolRecovery = RunScriptedToolRecovery(doc);
        success = success && scriptedToolRecovery.Ok;

        var viewportCaptureAwareness = RunViewportCaptureAwareness(doc);
        success = success && viewportCaptureAwareness.Ok;

        var skillSystem = RunSkillSystemSelfTest(doc);
        success = success && skillSystem.Ok;
        var documentMemory = RunDocumentMemoryRoundTrip(doc);
        success = success && documentMemory.Ok;

        var payload = new
        {
            ok = success,
            command = EnglishName,
            timestampUtc = DateTimeOffset.UtcNow,
            rhinoVersion = RhinoApp.Version?.ToString(),
            pluginAssembly = typeof(RhinoAgentPlugin).Assembly.GetName().Version?.ToString(),
            configPath = AgentConfigStore.ConfigPath,
            document = new
            {
                name = doc.Name ?? "(unsaved)",
                path = doc.Path ?? "(unsaved)",
                units = doc.ModelUnitSystem.ToString(),
                objectCount = doc.Objects.Count,
                layerCount = doc.Layers.Count,
                summary = RhinoDocumentSummarizer.Summarize(doc)
            },
            parser = new
            {
                visibleText = parsed.VisibleText,
                toolCallCount = parsed.ToolCalls.Count,
                firstTool = parsed.ToolCalls.FirstOrDefault()?.Tool
            },
            manualCommandRouting = new
            {
                lineRecognized,
                lineMatchedBy,
                lineScript,
                prefixedRecognized,
                prefixedMatchedBy,
                prefixedScript,
                aliasCount = aliasNames.Length,
                aliasProbe,
                aliasRecognized
            },
            scriptedToolRecovery,
            viewportCaptureAwareness,
            skillSystem,
            documentMemory,
            commands = new[]
            {
                "Agent",
                "AgentMemory",
                "AgentLogin",
                "AgentStatus",
                "AgentConfig",
                "AgentSelfTest",
                "AgentProviderSelfTest",
                "AgentPromptSelfTest"
            },
            registeredCommands
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        RhinoApp.WriteLine(success
            ? $"RhinoAgent self-test wrote {outputPath}"
            : $"RhinoAgent self-test failed; see {outputPath}");

        return success ? Result.Success : Result.Failure;
    }

    public static string GetOutputPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "self-test.json");

    private static SkillSystemSelfTestResult RunSkillSystemSelfTest(RhinoDoc doc)
    {
        var root = Path.Combine(Path.GetTempPath(), "RhinoAgent", "skill-self-test-" + Guid.NewGuid().ToString("N"));
        var provider = new ScriptedSkillUseProvider();
        Exception? exception = null;
        IReadOnlyList<SkillOperationResult> demoResults = [];
        ToolExecutionResult? createToolResult = null;
        ToolExecutionResult? readToolResult = null;
        ToolExecutionResult? unsafeToolResult = null;
        IReadOnlyList<SkillInfo> skills = [];
        IReadOnlyList<SkillContext> selected = [];
        AgentTurnResult? turnResult = null;

        try
        {
            var store = new SkillStore(root);
            demoResults = DemoSkillInstaller.Install(store, overwrite: false);

            var config = new AgentConfig
            {
                PermissionMode = AgentPermissionMode.FullAccess,
                ProviderProcessMode = AgentProviderProcessMode.Stateless,
                MaxToolRounds = 2
            };
            var toolHost = new RhinoToolHost(doc, config, store);
            var approvals = new ApprovalService(config);

            createToolResult = toolHost.ExecuteAsync(new ToolCallRequest
            {
                Tool = "create_skill",
                Arguments = new Dictionary<string, object?>
                {
                    ["name"] = "general-note-polisher",
                    ["description"] = "Polish short project notes into concise next actions. Use when the user asks to rewrite notes, clarify decisions, or turn rough notes into action items.",
                    ["files"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["path"] = "SKILL.md",
                                ["content"] =
                                    """
                                    ---
                                    name: general-note-polisher
                                    description: Polish short project notes into concise next actions. Use when the user asks to rewrite notes, clarify decisions, or turn rough notes into action items.
                                    ---

                                    # General Note Polisher

                                    Preserve named people, tools, dates, and commitments.
                                    Convert vague follow-ups into explicit action items when the source text supports it.
                                    Keep the final output concise and ready to paste.
                                    """
                            }
                        }
                }
            })
                .GetAwaiter()
                .GetResult();

            readToolResult = toolHost.ExecuteAsync(new ToolCallRequest
            {
                Tool = "read_skill_file",
                Arguments = new Dictionary<string, object?>
                {
                    ["name"] = "general-note-polisher",
                    ["path"] = "SKILL.md"
                }
            })
                .GetAwaiter()
                .GetResult();

            unsafeToolResult = toolHost.ExecuteAsync(new ToolCallRequest
            {
                Tool = "create_skill",
                Arguments = new Dictionary<string, object?>
                {
                    ["name"] = "unsafe-skill",
                    ["description"] = "This intentionally fails because it tries to write outside the skill folder.",
                    ["files"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["path"] = "../escape.txt",
                                ["content"] = "bad"
                            }
                        }
                }
            })
                .GetAwaiter()
                .GetResult();

            skills = store.ListSkills();
            selected = store.SelectRelevantSkills("Create a parametric facade grid form study in Rhino.", 3);
            var session = new AgentSession(doc, config, provider, toolHost, approvals, store);
            turnResult = session.RunUserTurnAsync(
                    "Use parametric form study to plan a facade grid.",
                    forcedSkillNames: ["parametric-form-study"])
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        var demosOk = demoResults.Count == 3 && demoResults.All(result => result.Success);
        var createToolOk = createToolResult?.Success == true;
        var readToolOk = readToolResult?.Success == true
            && readToolResult.Output.Contains("general-note-polisher", StringComparison.OrdinalIgnoreCase);
        var unsafeRejected = unsafeToolResult?.Success == false;
        var selectedParametric = selected.Any(skill => skill.Name == "parametric-form-study");
        var promptHadSkill = provider.Prompts.Any(prompt =>
            prompt.Contains("--- skill: parametric-form-study ---", StringComparison.OrdinalIgnoreCase)
            && prompt.Contains("layered-box-grid.csx", StringComparison.OrdinalIgnoreCase));
        var ok = exception is null
            && demosOk
            && createToolOk
            && readToolOk
            && unsafeRejected
            && skills.Count >= 4
            && selectedParametric
            && turnResult?.Success == true
            && promptHadSkill;

        return new SkillSystemSelfTestResult(
            ok,
            root,
            demoResults.Select(result => result.Message).ToArray(),
            skills.Select(skill => skill.Name).ToArray(),
            selected.Select(skill => skill.Name).ToArray(),
            createToolOk,
            readToolOk,
            unsafeRejected,
            turnResult?.Success ?? false,
            provider.Prompts.Count,
            promptHadSkill,
            FirstNonEmpty(exception?.Message, turnResult?.Error));
    }

    private static ViewportCaptureAwarenessResult RunViewportCaptureAwareness(RhinoDoc doc)
    {
        var marker = Guid.NewGuid().ToString("N");
        var beforeIds = doc.Objects
            .Where(obj => !obj.IsDeleted)
            .Select(obj => obj.Id)
            .ToHashSet();
        var selectedBefore = doc.Objects
            .Where(obj => !obj.IsDeleted && obj.IsSelected(false) > 0)
            .Select(obj => obj.Id)
            .ToArray();
        var provider = new ScriptedViewportCaptureProvider();
        AgentTurnResult? turnResult = null;
        Exception? exception = null;
        Guid boxId = Guid.Empty;
        double nonBackgroundRatio = 0;
        var visualVariationDetected = false;

        try
        {
            var attrs = new ObjectAttributes { Name = "RhinoAgent Visual Self-Test Box" };
            attrs.SetUserString(SelfTestMarkerKey, marker);
            var brep = new Box(
                Plane.WorldXY,
                new Interval(0, 10),
                new Interval(0, 10),
                new Interval(0, 10)).ToBrep();
            boxId = doc.Objects.AddBrep(brep, attrs);
            if (boxId == Guid.Empty)
                throw new InvalidOperationException("Failed to add viewport-capture self-test box.");

            doc.Objects.UnselectAll();
            doc.Objects.Select(boxId);
            doc.Views.Redraw();

            var config = new AgentConfig
            {
                PermissionMode = AgentPermissionMode.FullAccess,
                ProviderProcessMode = AgentProviderProcessMode.Stateless,
                EnableDocumentMemory = false,
                MaxToolRounds = 3
            };
            var toolHost = new RhinoToolHost(doc, config);
            var approvals = new ApprovalService(config);
            var session = new AgentSession(doc, config, provider, toolHost, approvals);
            turnResult = session.RunUserTurnAsync(
                    "Self-test visual awareness: capture the selected box and describe the visual result.")
                .GetAwaiter()
                .GetResult();

            if (!string.IsNullOrWhiteSpace(provider.ObservedManifestPath))
                visualVariationDetected = TryReadNonBackgroundRatio(provider.ObservedManifestPath, out nonBackgroundRatio)
                    && nonBackgroundRatio > 0;
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            DeleteObjectsCreatedBySelfTest(doc, beforeIds, marker);
            doc.Objects.UnselectAll();
            foreach (var id in selectedBefore)
            {
                var obj = doc.Objects.FindId(id);
                if (obj is not null && !obj.IsDeleted)
                    doc.Objects.Select(id);
            }
            doc.Views.Redraw();
        }

        var captureResultFedBack = HasToolResultPrompt(provider.Prompts, "capture_viewport", true);
        var imageExists = !string.IsNullOrWhiteSpace(provider.ObservedImagePath) && File.Exists(provider.ObservedImagePath);
        var manifestExists = !string.IsNullOrWhiteSpace(provider.ObservedManifestPath) && File.Exists(provider.ObservedManifestPath);
        var imageBytes = imageExists ? new FileInfo(provider.ObservedImagePath!).Length : 0;
        var manifestBytes = manifestExists ? new FileInfo(provider.ObservedManifestPath!).Length : 0;
        var visibleText = turnResult?.VisibleText ?? "";
        var responseUnderstoodCapture = visibleText.Contains("nonblank", StringComparison.OrdinalIgnoreCase)
            && visibleText.Contains("box", StringComparison.OrdinalIgnoreCase)
            && visibleText.Contains("visual check", StringComparison.OrdinalIgnoreCase);
        var ok = exception is null
            && turnResult?.Success == true
            && turnResult.ToolCallCount == 1
            && turnResult.ToolResultCount == 1
            && !turnResult.StoppedAfterToolLimit
            && captureResultFedBack
            && provider.ObservedCaptureSuccess
            && provider.ObservedPixelSummary
            && imageExists
            && imageBytes > 0
            && manifestExists
            && manifestBytes > 0
            && visualVariationDetected
            && responseUnderstoodCapture;

        return new ViewportCaptureAwarenessResult(
            ok,
            marker,
            boxId,
            provider.Prompts.Count,
            turnResult?.Success ?? false,
            turnResult?.ToolCallCount ?? 0,
            turnResult?.ToolResultCount ?? 0,
            turnResult?.StoppedAfterToolLimit ?? false,
            captureResultFedBack,
            provider.ObservedCaptureSuccess,
            provider.ObservedPixelSummary,
            provider.ObservedImagePath,
            imageExists,
            imageBytes,
            provider.ObservedManifestPath,
            manifestExists,
            manifestBytes,
            nonBackgroundRatio,
            visualVariationDetected,
            responseUnderstoodCapture,
            visibleText,
            FirstNonEmpty(exception?.Message, turnResult?.Error));
    }

    private static ScriptedToolRecoveryResult RunScriptedToolRecovery(RhinoDoc doc)
    {
        var marker = Guid.NewGuid().ToString("N");
        var beforeIds = doc.Objects
            .Where(obj => !obj.IsDeleted)
            .Select(obj => obj.Id)
            .ToHashSet();
        var provider = new ScriptedToolRecoveryProvider(marker);
        AgentTurnResult? turnResult = null;
        Exception? exception = null;
        ScriptedBoxProbe boxProbe;

        try
        {
            var config = new AgentConfig
            {
                PermissionMode = AgentPermissionMode.FullAccess,
                ProviderProcessMode = AgentProviderProcessMode.Stateless,
                EnableDocumentMemory = false,
                MaxToolRounds = 4
            };
            var toolHost = new RhinoToolHost(doc, config);
            var approvals = new ApprovalService(config);
            var session = new AgentSession(doc, config, provider, toolHost, approvals);
            turnResult = session.RunUserTurnAsync(
                    "Self-test scripted recovery: create a simple box after earlier tool attempts fail.")
                .GetAwaiter()
                .GetResult();
            boxProbe = ProbeSelfTestBox(doc, marker);
        }
        catch (Exception ex)
        {
            exception = ex;
            boxProbe = ProbeSelfTestBox(doc, marker);
        }
        finally
        {
            DeleteObjectsCreatedBySelfTest(doc, beforeIds, marker);
        }

        var runCommandResultFedBack = HasToolResultPrompt(provider.Prompts, "run_command", null);
        var runPythonResultFedBack = HasToolResultPrompt(provider.Prompts, "run_python", null);
        var executeCSharpSuccessFedBack = HasToolResultPrompt(provider.Prompts, "execute_csharp", true);
        var toolCallCount = turnResult?.ToolCallCount ?? 0;
        var toolResultCount = turnResult?.ToolResultCount ?? 0;
        var stoppedAfterToolLimit = turnResult?.StoppedAfterToolLimit ?? false;
        var ok = exception is null
            && turnResult?.Success == true
            && toolCallCount == 3
            && toolResultCount == 3
            && !stoppedAfterToolLimit
            && runCommandResultFedBack
            && runPythonResultFedBack
            && executeCSharpSuccessFedBack
            && boxProbe.Ok;

        return new ScriptedToolRecoveryResult(
            ok,
            marker,
            provider.Prompts.Count,
            turnResult?.Success ?? false,
            toolCallCount,
            toolResultCount,
            stoppedAfterToolLimit,
            runCommandResultFedBack,
            runPythonResultFedBack,
            executeCSharpSuccessFedBack,
            boxProbe,
            turnResult?.VisibleText ?? "",
            FirstNonEmpty(exception?.Message, turnResult?.Error));
    }

    private static ScriptedBoxProbe ProbeSelfTestBox(RhinoDoc doc, string marker)
    {
        var objects = doc.Objects
            .Where(obj => !obj.IsDeleted && obj.Attributes.GetUserString(SelfTestMarkerKey) == marker)
            .ToArray();
        var actual = BoundingBox.Empty;

        foreach (var obj in objects)
        {
            if (obj.Geometry is null)
                continue;
            actual.Union(obj.Geometry.GetBoundingBox(true));
        }

        var expected = new BoundingBox(Point3d.Origin, new Point3d(10, 10, 10));
        var tolerance = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var maxDeviation = actual.IsValid
            ? Math.Max(actual.Min.DistanceTo(expected.Min), actual.Max.DistanceTo(expected.Max))
            : double.PositiveInfinity;
        var ok = objects.Length == 1
            && actual.IsValid
            && maxDeviation <= tolerance;

        return new ScriptedBoxProbe(
            ok,
            objects.Length,
            actual.IsValid ? actual.ToString() : "(invalid)",
            expected.ToString(),
            maxDeviation,
            tolerance);
    }

    private static DocumentMemoryResult RunDocumentMemoryRoundTrip(RhinoDoc doc)
    {
        var before = AgentMemoryStore.Load(doc);
        var tempDir = Path.Combine(Path.GetTempPath(), "RhinoAgent", "memory-self-test", Guid.NewGuid().ToString("N"));
        var exportPath = Path.Combine(tempDir, "exported-memory.md");
        var importPath = Path.Combine(tempDir, "imported-memory.md");

        try
        {
            Directory.CreateDirectory(tempDir);
            AgentMemoryStore.Clear(doc);

            var created = AgentMemoryStore.EnsureCreated(doc);
            var createOk = created.Exists
                && created.Enabled
                && created.Markdown.Contains("## Project Intent", StringComparison.Ordinal)
                && created.Markdown.Contains(AgentMemoryMarkdown.AgentNotesBegin, StringComparison.Ordinal);

            var customMarkdown = $$"""
            # RhinoAgent Memory: Self-Test

            ## Project Intent
            - Preserve this user-authored intent.

            ## Modeling Conventions
            - Keep self-test geometry disposable.

            ## Constraints
            - Do not store live object counts here.

            ## Decisions
            - Memory lives in the active .3dm document.

            ## Current Tasks
            - Verify document memory round trips.

            ## Agent Notes
            {{AgentMemoryMarkdown.AgentNotesBegin}}
            - Old generated note.
            {{AgentMemoryMarkdown.AgentNotesEnd}}
            """;
            var saved = AgentMemoryStore.SaveUserMarkdown(doc, customMarkdown, "Self-test save.");
            var generated = AgentMemoryStore.ApplyGeneratedUpdate(
                doc,
                "- Generated durable note from self-test.",
                "Self-test compact memory summary.",
                "Self-test generated update.");
            var afterGenerated = AgentMemoryStore.Load(doc);
            var userSectionPreserved = afterGenerated.Markdown.Contains("Preserve this user-authored intent.", StringComparison.Ordinal);
            var generatedSectionUpdated = afterGenerated.Markdown.Contains("Generated durable note from self-test.", StringComparison.Ordinal)
                && !afterGenerated.Markdown.Contains("Old generated note.", StringComparison.Ordinal);
            var summaryStored = afterGenerated.PromptSummary == "Self-test compact memory summary.";
            var historyCaptured = afterGenerated.History.Count > 0;

            var undo = AgentMemoryStore.Undo(doc, 1);
            var afterUndo = AgentMemoryStore.Load(doc);
            var undoOk = undo.Changed
                && afterUndo.Markdown.Contains("Old generated note.", StringComparison.Ordinal)
                && afterUndo.Markdown.Contains("Preserve this user-authored intent.", StringComparison.Ordinal);

            var exportedPath = AgentMemoryStore.ExportMarkdown(doc, exportPath);
            var exportOk = File.Exists(exportedPath)
                && File.ReadAllText(exportedPath).Contains("Preserve this user-authored intent.", StringComparison.Ordinal);

            File.WriteAllText(importPath, customMarkdown.Replace("Preserve this user-authored intent.", "Imported intent."));
            var imported = AgentMemoryStore.ImportMarkdown(doc, importPath);
            var importOk = imported.Changed && AgentMemoryStore.Load(doc).Markdown.Contains("Imported intent.", StringComparison.Ordinal);

            var promptSmall = AgentMemoryPromptFormatter.FormatForPrompt(doc);
            var promptSmallOk = promptSmall.Contains("Embedded RhinoAgent memory", StringComparison.Ordinal)
                && promptSmall.Contains("Imported intent.", StringComparison.Ordinal);
            var promptPackage = AgentPromptBuilder.Build(
                doc,
                new AgentConfig { EnableDocumentMemory = true },
                [("user", "please update memory")],
                [],
                "test tools");
            var promptGuardrailOk = promptPackage.Contains("canonical project memory is embedded in the active Rhino .3dm document", StringComparison.Ordinal)
                && promptPackage.Contains("do not create or edit AGENTS.md, MEMORY.md, or any sidecar markdown file with write_file", StringComparison.Ordinal)
                && promptPackage.Contains("incrementally indexes completed session turns", StringComparison.Ordinal);

            var largeMarkdown = customMarkdown + System.Environment.NewLine + new string('x', AgentMemoryMarkdown.PromptMemoryCharacterLimit + 200);
            AgentMemoryStore.SaveUserMarkdown(doc, largeMarkdown, "Self-test large memory.");
            var promptLarge = AgentMemoryPromptFormatter.FormatForPrompt(doc);
            var promptLargeOk = promptLarge.Contains("larger than the prompt limit", StringComparison.Ordinal)
                && promptLarge.Contains("compact memory summary", StringComparison.Ordinal);

            AgentMemoryStore.SaveUserMarkdown(doc, customMarkdown, "Self-test maintenance base.");
            var maintenanceUpdate = new AgentMemoryUpdateService(
                    doc,
                    new AgentConfig { EnableDocumentMemory = true, ShowDebugMessages = false },
                    () => new ScriptedMemoryProvider(true))
                .RefreshAsync()
                .GetAwaiter()
                .GetResult();
            var afterMaintenance = AgentMemoryStore.Load(doc);
            var maintenanceOk = maintenanceUpdate.Updated
                && afterMaintenance.Markdown.Contains("Scripted maintenance note.", StringComparison.Ordinal)
                && afterMaintenance.Markdown.Contains("Preserve this user-authored intent.", StringComparison.Ordinal);

            var conversationIndex = new AgentConversationIndex();
            var indexedTurnResult = new AgentTurnResult(
                true,
                "Scripted provider",
                "self-test",
                "self-test-provider-session",
                null,
                0,
                "",
                "The millimeter-unit decision is recorded.",
                0,
                0,
                false,
                null);
            var indexAdded = conversationIndex.TryAdd(
                "Remember our decision to use millimeters for this model.",
                indexedTurnResult,
                out _);
            var duplicateRejected = !conversationIndex.TryAdd(
                "Remember our decision to use millimeters for this model.",
                indexedTurnResult,
                out _);
            var indexBatch = conversationIndex.GetNextBatch();
            var indexingProvider = new ScriptedMemoryProvider(
                true,
                "- Scripted indexed conversation note.",
                "Scripted indexed conversation summary.");
            var indexUpdate = new AgentMemoryUpdateService(
                    doc,
                    new AgentConfig { EnableDocumentMemory = true, ShowDebugMessages = false },
                    () => indexingProvider)
                .IndexConversationBatchAsync(indexBatch)
                .GetAwaiter()
                .GetResult();
            if (indexUpdate.Completed)
                conversationIndex.MarkIndexed(indexBatch);
            var afterIndex = AgentMemoryStore.Load(doc);
            var batchingIndex = new AgentConversationIndex();
            var batchingAddsOk = Enumerable.Range(1, AgentConversationIndex.AutomaticFlushTurnCount)
                .All(index => batchingIndex.TryAdd(
                    $"Conversation batch turn {index}.",
                    indexedTurnResult with { VisibleText = $"Batch response {index}." },
                    out _));
            var batchingOk = batchingAddsOk
                && batchingIndex.ShouldFlushAutomatically
                && batchingIndex.GetNextBatch().Count == AgentConversationIndex.AutomaticFlushTurnCount;
            var conversationIndexOk = indexAdded
                && duplicateRejected
                && batchingOk
                && indexBatch.Count == 1
                && indexUpdate.Completed
                && indexUpdate.Updated
                && conversationIndex.PendingTurnCount == 0
                && indexingProvider.LastPrompt.Contains("Conversation index batch:", StringComparison.Ordinal)
                && indexingProvider.LastPrompt.Contains("Remember our decision to use millimeters", StringComparison.Ordinal)
                && afterIndex.Markdown.Contains("Scripted indexed conversation note.", StringComparison.Ordinal)
                && afterIndex.Markdown.Contains("Preserve this user-authored intent.", StringComparison.Ordinal);

            var beforeNoUpdateHash = afterIndex.CurrentHash;
            var noUpdate = new AgentMemoryUpdateService(
                    doc,
                    new AgentConfig { EnableDocumentMemory = true, ShowDebugMessages = false },
                    () => new ScriptedMemoryProvider(false))
                .RefreshAsync()
                .GetAwaiter()
                .GetResult();
            var afterNoUpdate = AgentMemoryStore.Load(doc);
            var noUpdateOk = !noUpdate.Updated && afterNoUpdate.CurrentHash == beforeNoUpdateHash;

            var disabled = AgentMemoryStore.SetEnabled(doc, false);
            var disabledPrompt = AgentMemoryPromptFormatter.FormatForPrompt(doc);
            var disabledOk = disabled.Changed
                && disabledPrompt.Contains("disabled", StringComparison.OrdinalIgnoreCase);

            var ok = createOk
                && saved.Changed
                && generated.Changed
                && userSectionPreserved
                && generatedSectionUpdated
                && summaryStored
                && historyCaptured
                && undoOk
                && exportOk
                && importOk
                && promptSmallOk
                && promptGuardrailOk
                && promptLargeOk
                && maintenanceOk
                && conversationIndexOk
                && noUpdateOk
                && disabledOk;

            return new DocumentMemoryResult(
                ok,
                createOk,
                saved.Changed,
                generated.Changed,
                userSectionPreserved,
                generatedSectionUpdated,
                summaryStored,
                historyCaptured,
                undoOk,
                exportOk,
                importOk,
                promptSmallOk,
                promptGuardrailOk,
                promptLargeOk,
                maintenanceOk,
                conversationIndexOk,
                noUpdateOk,
                disabledOk,
                exportedPath,
                null);
        }
        catch (Exception ex)
        {
            return new DocumentMemoryResult(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                exportPath,
                ex.Message);
        }
        finally
        {
            AgentMemoryStore.Restore(doc, before);
            TryDeleteDirectory(tempDir);
        }
    }

    private static void DeleteObjectsCreatedBySelfTest(RhinoDoc doc, HashSet<Guid> beforeIds, string marker)
    {
        var ids = doc.Objects
            .Where(obj => !obj.IsDeleted
                && (!beforeIds.Contains(obj.Id) || obj.Attributes.GetUserString(SelfTestMarkerKey) == marker))
            .Select(obj => obj.Id)
            .ToArray();

        foreach (var id in ids)
            doc.Objects.Delete(id, true);

        if (ids.Length > 0)
            doc.Views.Redraw();
    }

    private static bool HasToolResultPrompt(IReadOnlyList<string> prompts, string tool, bool? success)
    {
        var toolNeedle = $"- tool: {tool}";
        foreach (var prompt in prompts)
        {
            var index = prompt.IndexOf(toolNeedle, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            if (success is null)
                return true;

            var next = prompt.IndexOf("- tool:", index + toolNeedle.Length, StringComparison.OrdinalIgnoreCase);
            var block = next >= 0 ? prompt[index..next] : prompt[index..];
            if (block.Contains($"success: {success.Value}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Self-test cleanup is best-effort.
        }
    }

    private static bool TryReadNonBackgroundRatio(string manifestPath, out double ratio)
    {
        ratio = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("captures", out var captures)
                || captures.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var capture in captures.EnumerateArray())
            {
                if (capture.TryGetProperty("pixels", out var pixels)
                    && pixels.TryGetProperty("nonBackgroundRatio", out var value)
                    && value.ValueKind == JsonValueKind.Number)
                {
                    ratio = Math.Max(ratio, value.GetDouble());
                }
            }

            return ratio > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string[] GetAliasNames()
    {
        try
        {
            return CommandAliasList.GetNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record ScriptedToolRecoveryResult(
        bool Ok,
        string Marker,
        int ProviderPromptCount,
        bool TurnSuccess,
        int ToolCallCount,
        int ToolResultCount,
        bool StoppedAfterToolLimit,
        bool RunCommandResultFedBack,
        bool RunPythonResultFedBack,
        bool ExecuteCSharpSuccessFedBack,
        ScriptedBoxProbe Box,
        string VisibleText,
        string? Error);

    private sealed record ScriptedBoxProbe(
        bool Ok,
        int ObjectCount,
        string ActualBoundingBox,
        string ExpectedBoundingBox,
        double MaxDeviation,
        double Tolerance);

    private sealed record ViewportCaptureAwarenessResult(
        bool Ok,
        string Marker,
        Guid BoxId,
        int ProviderPromptCount,
        bool TurnSuccess,
        int ToolCallCount,
        int ToolResultCount,
        bool StoppedAfterToolLimit,
        bool CaptureResultFedBack,
        bool ObservedCaptureSuccess,
        bool ObservedPixelSummary,
        string? ImagePath,
        bool ImageExists,
        long ImageBytes,
        string? ManifestPath,
        bool ManifestExists,
        long ManifestBytes,
        double NonBackgroundRatio,
        bool VisualVariationDetected,
        bool ResponseUnderstoodCapture,
        string VisibleText,
        string? Error);

    private sealed record SkillSystemSelfTestResult(
        bool Ok,
        string Root,
        IReadOnlyList<string> DemoInstallMessages,
        IReadOnlyList<string> SkillNames,
        IReadOnlyList<string> SelectedSkillNames,
        bool CreateToolOk,
        bool ReadToolOk,
        bool UnsafePathRejected,
        bool TurnSuccess,
        int ProviderPromptCount,
        bool PromptHadSkill,
        string? Error);

    private sealed record DocumentMemoryResult(
        bool Ok,
        bool CreateOk,
        bool SaveChanged,
        bool GeneratedChanged,
        bool UserSectionPreserved,
        bool GeneratedSectionUpdated,
        bool SummaryStored,
        bool HistoryCaptured,
        bool UndoOk,
        bool ExportOk,
        bool ImportOk,
        bool PromptSmallOk,
        bool PromptGuardrailOk,
        bool PromptLargeOk,
        bool MaintenanceOk,
        bool ConversationIndexOk,
        bool NoUpdateOk,
        bool DisabledOk,
        string ExportPath,
        string? Error);

    private sealed class ScriptedToolRecoveryProvider : IAgentProvider
    {
        private readonly string _marker;
        private readonly List<string> _prompts = [];
        private int _turn;

        public ScriptedToolRecoveryProvider(string marker)
        {
            _marker = marker;
        }

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public IReadOnlyList<string> Prompts => _prompts;

        public Task<AgentProviderResult> RunPromptAsync(
            string prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt);
            _turn++;
            progress(new AgentProgress($"scripted self-test turn {_turn}"));

            var text = _turn switch
            {
                1 => WithTool(
                    "Creating a simple box. I will try a native command first.",
                    ToolCall("run_command", new Dictionary<string, object?>
                    {
                        ["command"] = "_Box 0,0,0 10,10,10",
                        ["echo"] = false
                    })),
                2 => WithTool(
                    "The command result was not enough, so I will try Python.",
                    ToolCall("run_python", new Dictionary<string, object?>
                    {
                        ["script"] = "print('rhino agent python probe')"
                    })),
                3 => WithTool(
                    "I will create the box directly with RhinoCommon.",
                    ToolCall("execute_csharp", new Dictionary<string, object?>
                    {
                        ["code"] = CreateBoxScript()
                    })),
                _ => "Created the self-test box."
            };

            return Task.FromResult(new AgentProviderResult(
                text,
                "self-test",
                "scripted-tool-recovery",
                null,
                0,
                ""));
        }

        public void Reset()
        {
            _turn = 0;
            _prompts.Clear();
        }

        public void Dispose()
        {
        }

        private string CreateBoxScript() =>
            $$"""
            var attrs = new ObjectAttributes { Name = "RhinoAgent Self-Test Box" };
            attrs.SetUserString("{{SelfTestMarkerKey}}", "{{_marker}}");
            var brep = new Box(
                Plane.WorldXY,
                new Interval(0, 10),
                new Interval(0, 10),
                new Interval(0, 10)).ToBrep();
            var id = doc.Objects.AddBrep(brep, attrs);
            if (id == Guid.Empty)
                throw new InvalidOperationException("Failed to add self-test box.");
            doc.Views.Redraw();
            output.WriteLine($"created_box_id={id}");
            """;

        private static ToolCallRequest ToolCall(string tool, Dictionary<string, object?> arguments) =>
            new()
            {
                Tool = tool,
                Arguments = arguments
            };

        private static string WithTool(string visibleText, ToolCallRequest call)
        {
            var envelope = new ToolCallEnvelope
            {
                ToolCalls = [call]
            };
            return $"""
                {visibleText}
                <rhino-agent>{JsonSerializer.Serialize(envelope, JsonOptions.Loose)}</rhino-agent>
                """;
        }
    }

    private sealed class ScriptedSkillUseProvider : IAgentProvider
    {
        private readonly List<string> _prompts = [];

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted skill-use self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public IReadOnlyList<string> Prompts => _prompts;

        public Task<AgentProviderResult> RunPromptAsync(
            string prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt);
            progress(new AgentProgress("scripted skill-use self-test turn"));

            var sawSkill = prompt.Contains("--- skill: parametric-form-study ---", StringComparison.OrdinalIgnoreCase);
            var text = sawSkill
                ? "The parametric form study skill is loaded and ready to guide the facade grid."
                : "No matching skill instructions were loaded.";

            return Task.FromResult(new AgentProviderResult(
                text,
                "self-test",
                "scripted-skill-use",
                null,
                0,
                ""));
        }

        public void Reset()
        {
            _prompts.Clear();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedViewportCaptureProvider : IAgentProvider
    {
        private readonly List<string> _prompts = [];
        private int _turn;

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted viewport-capture self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public IReadOnlyList<string> Prompts => _prompts;
        public bool ObservedCaptureSuccess { get; private set; }
        public bool ObservedPixelSummary { get; private set; }
        public string? ObservedImagePath { get; private set; }
        public string? ObservedManifestPath { get; private set; }

        public Task<AgentProviderResult> RunPromptAsync(
            string prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt);
            _turn++;
            progress(new AgentProgress($"scripted viewport-capture self-test turn {_turn}"));

            var text = _turn switch
            {
                1 => WithTool(
                    "I will capture the selected box from the active Rhino viewport.",
                    ToolCall("capture_viewport", new Dictionary<string, object?>
                    {
                        ["views"] = "active",
                        ["display_mode"] = "shaded",
                        ["fit"] = "extents",
                        ["width"] = 1024,
                        ["height"] = 768,
                        ["draw_grid"] = false,
                        ["draw_axes"] = false,
                        ["selected_only"] = false
                    })),
                _ => ObserveCaptureAndRespond(prompt)
            };

            return Task.FromResult(new AgentProviderResult(
                text,
                "self-test",
                "scripted-viewport-capture",
                null,
                0,
                ""));
        }

        public void Reset()
        {
            _turn = 0;
            _prompts.Clear();
            ObservedCaptureSuccess = false;
            ObservedPixelSummary = false;
            ObservedImagePath = null;
            ObservedManifestPath = null;
        }

        public void Dispose()
        {
        }

        private string ObserveCaptureAndRespond(string prompt)
        {
            ObservedCaptureSuccess = prompt.Contains("- tool: capture_viewport", StringComparison.OrdinalIgnoreCase)
                && prompt.Contains("success: True", StringComparison.OrdinalIgnoreCase);
            ObservedPixelSummary = prompt.Contains("nonBackgroundRatio", StringComparison.OrdinalIgnoreCase);
            ObservedManifestPath = ExtractJsonStringProperty(prompt, "manifestPath");
            ObservedImagePath = ExtractFirstArrayString(prompt, "imagePaths");

            return ObservedCaptureSuccess && ObservedPixelSummary
                ? "The viewport capture succeeded. The returned image and manifest show a nonblank visual capture of the selected box, so the visual check is usable."
                : "The viewport capture did not provide enough image metadata to verify the selected box visually.";
        }

        private static string? ExtractJsonStringProperty(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"",
                RegexOptions.IgnoreCase);
            return match.Success ? DecodeJsonString(match.Groups[1].Value) : null;
        }

        private static string? ExtractFirstArrayString(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\\[\\s*\"((?:\\\\.|[^\"])*)\"",
                RegexOptions.IgnoreCase);
            return match.Success ? DecodeJsonString(match.Groups[1].Value) : null;
        }

        private static string? DecodeJsonString(string escaped)
        {
            try
            {
                return JsonSerializer.Deserialize<string>($"\"{escaped}\"");
            }
            catch
            {
                return null;
            }
        }

        private static ToolCallRequest ToolCall(string tool, Dictionary<string, object?> arguments) =>
            new()
            {
                Tool = tool,
                Arguments = arguments
            };

        private static string WithTool(string visibleText, ToolCallRequest call)
        {
            var envelope = new ToolCallEnvelope
            {
                ToolCalls = [call]
            };
            return $"""
                {visibleText}
                <rhino-agent>{JsonSerializer.Serialize(envelope, JsonOptions.Loose)}</rhino-agent>
                """;
        }
    }

    private sealed class ScriptedMemoryProvider : IAgentProvider
    {
        private readonly bool _update;
        private readonly string _agentNotes;
        private readonly string _summary;

        public ScriptedMemoryProvider(
            bool update,
            string agentNotes = "- Scripted maintenance note.",
            string summary = "Scripted maintenance summary.")
        {
            _update = update;
            _agentNotes = agentNotes;
            _summary = summary;
        }

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted memory self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public string LastPrompt { get; private set; } = "";

        public Task<AgentProviderResult> RunPromptAsync(
            string prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPrompt = prompt;
            var text = _update
                ? JsonSerializer.Serialize(new
                {
                    update = true,
                    agentNotes = _agentNotes,
                    summary = _summary,
                    reason = "Scripted memory self-test update."
                })
                : JsonSerializer.Serialize(new
                {
                    update = false,
                    agentNotes = "",
                    summary = "",
                    reason = "No durable memory changes in scripted test."
                });
            return Task.FromResult(new AgentProviderResult(text, "self-test", "scripted-memory", null, 0, ""));
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

}
