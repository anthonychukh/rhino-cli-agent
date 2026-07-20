using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using RhinoAgent.Attachments;
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

        var modelValidation = RunModelValidationSelfTest();
        success = success && modelValidation.Ok;

        var scriptedToolRecovery = RunScriptedToolRecovery(doc);
        success = success && scriptedToolRecovery.Ok;

        var announcedActionContinuation = RunAnnouncedActionContinuation(doc);
        success = success && announcedActionContinuation.Ok;

        var viewportCaptureAwareness = RunViewportCaptureAwareness(doc);
        success = success && viewportCaptureAwareness.Ok;

        var attachments = RunAttachmentSelfTest(doc);
        success = success && attachments.Ok;

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
            modelValidation,
            scriptedToolRecovery,
            announcedActionContinuation,
            viewportCaptureAwareness,
            attachments,
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

    private static AttachmentSelfTestResult RunAttachmentSelfTest(RhinoDoc doc)
    {
        var root = Path.Combine(Path.GetTempPath(), "RhinoAgent", "attachment-self-test-" + Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(root, "one-pixel.png");
        var firstStepPath = Path.Combine(root, "first model.stp");
        var secondStepPath = Path.Combine(root, "second model.STP");
        var modelPath = Path.Combine(root, "reference model.3dm");
        var stlPath = Path.Combine(root, "triangle.stl");
        var fbxPath = Path.Combine(root, "triangle.fbx");
        var unknownPath = Path.Combine(root, "mystery.custom-format");
        var extensionlessPath = Path.Combine(root, "README-NO-EXTENSION");
        var attachmentRoot = Path.Combine(root, "owned-temp");
        var provider = new ScriptedAttachmentProvider();
        AgentTurnResult? turnResult = null;
        Exception? exception = null;
        var placeholderResolved = false;
        var allFilesAccepted = false;
        var extensionCountersOk = false;
        var promptManifestOk = false;
        var modelInspectionOk = false;
        var stlInspectionOk = false;
        var fbxInspectionOk = false;
        var comparisonOk = false;
        var binaryFallbackOk = false;
        var activeDocumentUnchanged = false;
        var temporaryCleaned = false;
        var disposeCleanupOk = false;
        var staleCleanupOk = false;
        var originalsPreserved = false;
        var importToolOk = false;
        var fbxImportToolOk = false;
        var importToolOutput = "";
        var importStagingCleaned = false;
        var importAlwaysPrompts = false;
        var claudeStreamJsonOk = false;
        var codexInputOk = false;
        var tempPath = "";
        AgentAttachmentStore? store = null;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(
                imagePath,
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            File.WriteAllText(firstStepPath, "ISO-10303-21; HEADER; FILE_DESCRIPTION(('placeholder one'),'2;1'); ENDSEC; END-ISO-10303-21;");
            File.WriteAllText(secondStepPath, "ISO-10303-21; HEADER; FILE_DESCRIPTION(('placeholder two'),'2;1'); ENDSEC; END-ISO-10303-21;");
            File.WriteAllText(
                stlPath,
                "solid triangle\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 10 0 0\nvertex 0 10 0\nendloop\nendfacet\nendsolid triangle\n");
            var fbxDocument = RhinoDoc.CreateHeadless(null)
                ?? throw new InvalidOperationException("Could not create the FBX fixture document.");
            try
            {
                var fbxMesh = new Mesh();
                fbxMesh.Vertices.Add(0, 0, 0);
                fbxMesh.Vertices.Add(10, 0, 0);
                fbxMesh.Vertices.Add(0, 10, 0);
                fbxMesh.Faces.AddFace(0, 1, 2);
                fbxMesh.Normals.ComputeNormals();
                fbxDocument.Objects.AddMesh(fbxMesh);
                if (!FileFbx.Write(fbxPath, fbxDocument, new FileFbxWriteOptions()))
                    throw new InvalidOperationException("Could not write the FBX attachment fixture.");
            }
            finally
            {
                fbxDocument.Dispose();
            }
            File.WriteAllBytes(unknownPath, [0, 1, 2, 3, 65, 66, 67, 68, 0, 255]);
            File.WriteAllText(extensionlessPath, "Extensionless attachments are still accepted and interpreted as text when their bytes are text-like.");

            using (var file = new File3dm())
            {
                file.Settings.ModelUnitSystem = UnitSystem.Millimeters;
                file.Objects.AddBrep(new Box(new BoundingBox(0, 0, 0, 10, 20, 30)).ToBrep());
                if (!file.Write(modelPath, 8))
                    throw new InvalidOperationException("Could not write the 3DM attachment fixture.");
            }

            var staleDirectory = Path.Combine(attachmentRoot, "stale-session");
            Directory.CreateDirectory(staleDirectory);
            File.WriteAllText(Path.Combine(staleDirectory, "leftover.tmp"), "stale");
            Directory.SetLastWriteTimeUtc(staleDirectory, DateTime.UtcNow - TimeSpan.FromHours(48));
            store = new AgentAttachmentStore(attachmentRoot);
            staleCleanupOk = !Directory.Exists(staleDirectory);
            var composer = new AgentAttachmentComposer(store);
            tempPath = store.CreateTemporaryFilePath("ephemeral.bin");
            File.WriteAllBytes(tempPath, [9, 8, 7, 6]);
            if (!composer.TryAttachPath(tempPath, isTemporary: true, out var temporaryAttachment, out var temporaryError))
                throw new InvalidOperationException(temporaryError);

            var message = composer.Compose(
                $"Compare \"{firstStepPath}\" with \"{secondStepPath}\", inspect \"{modelPath}\", \"{stlPath}\", \"{fbxPath}\", \"{unknownPath}\", \"{extensionlessPath}\", and \"{imagePath}\".");
            placeholderResolved = message.Text.Contains("[.stp 1]", StringComparison.Ordinal)
                && message.Text.Contains("[.stp 2]", StringComparison.Ordinal)
                && message.Text.Contains("[.3dm 1]", StringComparison.Ordinal)
                && message.Text.Contains("[.stl 1]", StringComparison.Ordinal)
                && message.Text.Contains("[.fbx 1]", StringComparison.Ordinal)
                && message.Text.Contains("[.custom-format 1]", StringComparison.Ordinal)
                && message.Text.Contains("[file 1]", StringComparison.Ordinal)
                && message.Text.Contains("[.png 1]", StringComparison.Ordinal);
            extensionCountersOk = message.Attachments.Single(value => value.LocalPath == firstStepPath).Placeholder == "[.stp 1]"
                && message.Attachments.Single(value => value.LocalPath == secondStepPath).Placeholder == "[.stp 2]";
            allFilesAccepted = message.Attachments.Count == 8
                && message.Images.Count == 1
                && message.Attachments.Any(value => value.LocalPath == extensionlessPath && value.Placeholder == "[file 1]");

            var config = new AgentConfig
            {
                PermissionMode = AgentPermissionMode.FullAccess,
                ProviderProcessMode = AgentProviderProcessMode.Stateless,
                EnableDocumentMemory = false,
                MaxToolRounds = 2
            };
            var toolHost = new RhinoToolHost(doc, config, attachmentStore: store);
            var approvals = new ApprovalService(config);
            var session = new AgentSession(doc, config, provider, toolHost, approvals);

            var importCall = new ToolCallRequest
            {
                Tool = "import_attachment",
                Arguments = new Dictionary<string, object?> { ["attachment"] = "[.stl 1]" }
            };
            importAlwaysPrompts = toolHost.AlwaysRequiresPrompt(importCall.Tool)
                && approvals.RequiresPrompt(importCall, toolHost);
            var importDocument = RhinoDoc.CreateHeadless(null);
            if (importDocument is not null)
            {
                try
                {
                    var importHost = new RhinoToolHost(importDocument, config, attachmentStore: store);
                    var importResult = importHost.ExecuteAsync(importCall).GetAwaiter().GetResult();
                    importToolOutput = importResult.Output;
                    importToolOk = importResult.Success && importDocument.Objects.Count > 0;
                    var fbxImportResult = importHost.ExecuteAsync(new ToolCallRequest
                    {
                        Tool = "import_attachment",
                        Arguments = new Dictionary<string, object?> { ["attachment"] = "[.fbx 1]" }
                    }).GetAwaiter().GetResult();
                    importToolOutput += System.Environment.NewLine + fbxImportResult.Output;
                    fbxImportToolOk = fbxImportResult.Success && importDocument.Objects.Count > 1;
                    importStagingCleaned = !Directory.EnumerateFiles(
                        store.SessionRoot,
                        "*.3dm",
                        SearchOption.AllDirectories).Any();
                }
                finally
                {
                    importDocument.Dispose();
                }
            }

            var activeObjectCount = doc.Objects.Count;
            modelInspectionOk = ExecuteAttachmentTool(toolHost, "inspect_attachment", new Dictionary<string, object?>
            {
                ["attachment"] = "[.3dm 1]"
            }).Success;
            stlInspectionOk = ExecuteAttachmentTool(toolHost, "inspect_attachment", new Dictionary<string, object?>
            {
                ["attachment"] = "[.stl 1]"
            }).Success;
            fbxInspectionOk = ExecuteAttachmentTool(toolHost, "inspect_attachment", new Dictionary<string, object?>
            {
                ["attachment"] = "[.fbx 1]"
            }).Success;
            comparisonOk = ExecuteAttachmentTool(toolHost, "compare_attachments", new Dictionary<string, object?>
            {
                ["attachments"] = new[] { "[.3dm 1]", "[.stl 1]" }
            }).Success;
            var binaryResult = ExecuteAttachmentTool(toolHost, "inspect_attachment", new Dictionary<string, object?>
            {
                ["attachment"] = "[.custom-format 1]"
            });
            binaryFallbackOk = binaryResult.Success
                && binaryResult.Output.Contains("binary-probe", StringComparison.OrdinalIgnoreCase);
            activeDocumentUnchanged = doc.Objects.Count == activeObjectCount;
            turnResult = session.RunUserTurnAsync(message).GetAwaiter().GetResult();

            var firstPrompt = provider.Prompts.FirstOrDefault();
            if (firstPrompt is not null)
            {
                promptManifestOk = firstPrompt.Text.Contains("Attachments available for this turn:", StringComparison.Ordinal)
                    && firstPrompt.Text.Contains("[.stp 1]", StringComparison.Ordinal)
                    && firstPrompt.Text.Contains("[.stp 2]", StringComparison.Ordinal)
                    && firstPrompt.Text.Contains("[file 1]", StringComparison.Ordinal);
                using var claudeJson = JsonDocument.Parse(ClaudeCliProvider.BuildStreamJsonInput(firstPrompt));
                var content = claudeJson.RootElement
                    .GetProperty("message")
                    .GetProperty("content");
                var source = content[0].GetProperty("source");
                claudeStreamJsonOk = content.GetArrayLength() == 2
                    && content[0].GetProperty("type").GetString() == "image"
                    && source.GetProperty("type").GetString() == "base64"
                    && source.GetProperty("media_type").GetString() == "image/png"
                    && Convert.FromBase64String(source.GetProperty("data").GetString() ?? "").Length > 0
                    && content[1].GetProperty("type").GetString() == "text";

                var codexJson = JsonSerializer.Serialize(CodexAppServerProvider.BuildTurnInput(firstPrompt));
                using var codexInput = JsonDocument.Parse(codexJson);
                var items = codexInput.RootElement;
                codexInputOk = items.GetArrayLength() == 2
                    && items[0].GetProperty("type").GetString() == "localImage"
                    && items[0].GetProperty("path").GetString() == imagePath
                    && items[1].GetProperty("type").GetString() == "text";
            }

            store.ReleaseTemporary([temporaryAttachment]);
            temporaryCleaned = !File.Exists(tempPath)
                && !Directory.Exists(Path.GetDirectoryName(tempPath));

            var disposeRoot = Path.Combine(root, "dispose-cleanup");
            var disposeStore = new AgentAttachmentStore(disposeRoot);
            var disposeTempPath = disposeStore.CreateTemporaryFilePath("dispose-me.bin");
            File.WriteAllBytes(disposeTempPath, [1, 2, 3]);
            if (!disposeStore.TryRegister(disposeTempPath, isTemporary: true, out _, out var disposeError))
                throw new InvalidOperationException(disposeError);
            var disposeSessionRoot = disposeStore.SessionRoot;
            disposeStore.Dispose();
            disposeCleanupOk = !File.Exists(disposeTempPath) && !Directory.Exists(disposeSessionRoot);

            originalsPreserved = new[] { imagePath, firstStepPath, secondStepPath, modelPath, stlPath, fbxPath, unknownPath, extensionlessPath }
                .All(File.Exists);
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            store?.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Self-test cleanup is best effort.
            }
        }

        var imageCounts = provider.Prompts.Select(prompt => prompt.Images.Count).ToArray();
        var firstRoundOnly = imageCounts.SequenceEqual([1, 0]);
        var ok = exception is null
            && placeholderResolved
            && allFilesAccepted
            && extensionCountersOk
            && promptManifestOk
            && modelInspectionOk
            && stlInspectionOk
            && fbxInspectionOk
            && comparisonOk
            && binaryFallbackOk
            && activeDocumentUnchanged
            && temporaryCleaned
            && disposeCleanupOk
            && staleCleanupOk
            && originalsPreserved
            && importToolOk
            && fbxImportToolOk
            && importStagingCleaned
            && importAlwaysPrompts
            && claudeStreamJsonOk
            && codexInputOk
            && firstRoundOnly
            && turnResult?.Success == true;

        return new AttachmentSelfTestResult(
            ok,
            placeholderResolved,
            allFilesAccepted,
            extensionCountersOk,
            promptManifestOk,
            modelInspectionOk,
            stlInspectionOk,
            fbxInspectionOk,
            comparisonOk,
            binaryFallbackOk,
            activeDocumentUnchanged,
            temporaryCleaned,
            disposeCleanupOk,
            staleCleanupOk,
            originalsPreserved,
            importToolOk,
            fbxImportToolOk,
            importToolOutput,
            importStagingCleaned,
            importAlwaysPrompts,
            provider.Prompts.Count,
            imageCounts,
            firstRoundOnly,
            claudeStreamJsonOk,
            codexInputOk,
            turnResult?.Success ?? false,
            FirstNonEmpty(exception?.Message, turnResult?.Error));
    }

    private static ToolExecutionResult ExecuteAttachmentTool(
        RhinoToolHost toolHost,
        string tool,
        Dictionary<string, object?> arguments) =>
        toolHost.ExecuteAsync(new ToolCallRequest
        {
            Tool = tool,
            Arguments = arguments
        }).GetAwaiter().GetResult();

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

    private static AnnouncedActionContinuationResult RunAnnouncedActionContinuation(RhinoDoc doc)
    {
        var marker = Guid.NewGuid().ToString("N");
        var beforeIds = doc.Objects
            .Where(obj => !obj.IsDeleted)
            .Select(obj => obj.Id)
            .ToHashSet();
        var provider = new ScriptedAnnouncedActionProvider(marker);
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
                MaxToolRounds = 1
            };
            var toolHost = new RhinoToolHost(doc, config);
            var approvals = new ApprovalService(config);
            var session = new AgentSession(doc, config, provider, toolHost, approvals);
            turnResult = session.RunUserTurnAsync(
                    "Model the entire Art Deco building, starting with the main massing.")
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

        var continuationPromptSeen = provider.Prompts.Any(prompt =>
            prompt.Contains("Continuation required for this provider round:", StringComparison.Ordinal)
            && prompt.Contains("announced an action but contained no RhinoAgent tool call", StringComparison.Ordinal));
        var ok = exception is null
            && AgentSession.ShouldRecoverMissingAction(
                "Model the entire Art Deco building.",
                "I'll start modeling the Art Deco building with its main stepped massing.")
            && !AgentSession.ShouldRecoverMissingAction(
                "How would you model an Art Deco building?",
                "I'll start with the main massing, then describe the setbacks.")
            && !AgentSession.ShouldRecoverMissingAction(
                "Model the entire Art Deco building.",
                "The Art Deco building uses stepped massing and vertical setbacks.")
            && provider.Prompts.Count == 2
            && continuationPromptSeen
            && turnResult?.Success == true
            && turnResult.ToolCallCount == 1
            && turnResult.ToolResultCount == 1
            && !turnResult.StoppedAfterToolLimit
            && boxProbe.Ok;

        return new AnnouncedActionContinuationResult(
            ok,
            marker,
            provider.Prompts.Count,
            continuationPromptSeen,
            turnResult?.Success ?? false,
            turnResult?.ToolCallCount ?? 0,
            turnResult?.ToolResultCount ?? 0,
            turnResult?.StoppedAfterToolLimit ?? false,
            boxProbe,
            turnResult?.VisibleText ?? "",
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
                && promptPackage.Contains("separate non-blocking background worker", StringComparison.Ordinal);

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

            var backgroundIndexOk = RunBackgroundConversationIndexing(doc);
            var afterBackgroundIndex = AgentMemoryStore.Load(doc);
            var beforeNoUpdateHash = afterBackgroundIndex.CurrentHash;
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
                && backgroundIndexOk
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
                backgroundIndexOk,
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

    private static bool RunBackgroundConversationIndexing(RhinoDoc doc)
    {
        var config = new AgentConfig
        {
            EnableDocumentMemory = true,
            ShowDebugMessages = false,
            ShowUsageMessages = false,
            ProviderTurnTimeoutSeconds = 8
        };
        using var foregroundProvider = new ScriptedConversationProvider();
        var backgroundProvider = new BlockingMemoryProvider();
        var memoryUpdater = new AgentMemoryUpdateService(doc, config, () => backgroundProvider);
        var session = new AgentSession(
            doc,
            config,
            foregroundProvider,
            new RhinoToolHost(doc, config),
            new ApprovalService(config),
            memoryUpdater: memoryUpdater);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var turnResult = RhinoTaskPump.Run(
            cancellationToken => session.RunUserTurnAsync(
                "Remember that background conversation indexing must not block the next prompt.",
                cancellationToken),
            timeout.Token);
        stopwatch.Stop();

        var returnedBeforeRelease = turnResult.Success
            && !backgroundProvider.IsReleased
            && stopwatch.Elapsed < TimeSpan.FromSeconds(2);
        var providerStarted = RhinoTaskPump.Run(
            async cancellationToken =>
            {
                await backgroundProvider.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            timeout.Token);
        var workerWasRunning = session.IsConversationIndexing;

        backgroundProvider.Release();
        var workerCompleted = RhinoTaskPump.Run(
            async cancellationToken =>
            {
                await session.WaitForConversationIndexingAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            timeout.Token);
        var after = AgentMemoryStore.Load(doc);

        var backgroundCompletedOk = returnedBeforeRelease
            && providerStarted
            && workerWasRunning
            && workerCompleted
            && !session.IsConversationIndexing
            && session.PendingConversationIndexTurnCount == 0
            && after.Markdown.Contains("Background conversation indexing completed.", StringComparison.Ordinal)
            && after.Markdown.Contains("Preserve this user-authored intent.", StringComparison.Ordinal);
        if (!backgroundCompletedOk)
            return false;

        using var conflictForegroundProvider = new ScriptedConversationProvider();
        var conflictProvider = new BlockingMemoryProvider(
            "- Stale background note that must not be applied.",
            "Stale background summary that must not be applied.");
        var conflictSession = new AgentSession(
            doc,
            config,
            conflictForegroundProvider,
            new RhinoToolHost(doc, config),
            new ApprovalService(config),
            memoryUpdater: new AgentMemoryUpdateService(doc, config, () => conflictProvider));
        using var conflictTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var conflictTurn = RhinoTaskPump.Run(
            cancellationToken => conflictSession.RunUserTurnAsync(
                "Remember this conflict-detection self-test decision.",
                cancellationToken),
            conflictTimeout.Token);
        var conflictProviderStarted = RhinoTaskPump.Run(
            async cancellationToken =>
            {
                await conflictProvider.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            conflictTimeout.Token);

        var concurrentMarkdown = AgentMemoryStore.Load(doc).Markdown.Replace(
            "Preserve this user-authored intent.",
            "Preserve this user-authored intent. Concurrent edit marker.",
            StringComparison.Ordinal);
        var concurrentEdit = AgentMemoryStore.SaveUserMarkdown(
            doc,
            concurrentMarkdown,
            "Background index conflict self-test edit.");
        conflictProvider.Release();
        var conflictWorkerCompleted = RhinoTaskPump.Run(
            async cancellationToken =>
            {
                await conflictSession.WaitForConversationIndexingAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            conflictTimeout.Token);
        var afterConflict = AgentMemoryStore.Load(doc);

        return conflictTurn.Success
            && conflictProviderStarted
            && concurrentEdit.Changed
            && conflictWorkerCompleted
            && !conflictSession.IsConversationIndexing
            && conflictSession.PendingConversationIndexTurnCount == 1
            && afterConflict.Markdown.Contains("Concurrent edit marker.", StringComparison.Ordinal)
            && !afterConflict.Markdown.Contains("Stale background note", StringComparison.Ordinal);
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

    private static ModelValidationSelfTestResult RunModelValidationSelfTest()
    {
        using var response = JsonDocument.Parse(
            """
            {
              "id": "models",
              "result": {
                "data": [
                  { "id": "gpt-5.6-sol", "model": "gpt-5.6-sol" },
                  { "id": "gpt-5.5", "model": "gpt-5.5" }
                ],
                "nextCursor": "next-page"
              }
            }
            """);
        var parsedModels = CodexAppServerProvider.ReadAvailableModels(
            response.RootElement,
            out var nextCursor);
        var exact = ModelNameValidator.Validate("GPT-5.5", parsedModels);
        var typo = ModelNameValidator.Validate("gpt5.5", parsedModels);
        var catalogParsingOk = parsedModels.SequenceEqual(["gpt-5.6-sol", "gpt-5.5"])
            && nextCursor == "next-page";
        var exactMatchOk = exact.IsValid
            && exact.CanonicalName == "gpt-5.5"
            && exact.Suggestion is null;
        var typoRejected = !typo.IsValid && typo.CanonicalName is null;
        var closestSuggestionOk = typo.Suggestion == "gpt-5.5";

        return new ModelValidationSelfTestResult(
            catalogParsingOk && exactMatchOk && typoRejected && closestSuggestionOk,
            catalogParsingOk,
            exactMatchOk,
            typoRejected,
            closestSuggestionOk,
            typo.Suggestion);
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

    private sealed record ModelValidationSelfTestResult(
        bool Ok,
        bool CatalogParsingOk,
        bool ExactMatchOk,
        bool TypoRejected,
        bool ClosestSuggestionOk,
        string? SuggestedModel);

    private sealed record ScriptedBoxProbe(
        bool Ok,
        int ObjectCount,
        string ActualBoundingBox,
        string ExpectedBoundingBox,
        double MaxDeviation,
        double Tolerance);

    private sealed record AnnouncedActionContinuationResult(
        bool Ok,
        string Marker,
        int ProviderPromptCount,
        bool ContinuationPromptSeen,
        bool TurnSuccess,
        int ToolCallCount,
        int ToolResultCount,
        bool StoppedAfterToolLimit,
        ScriptedBoxProbe Box,
        string VisibleText,
        string? Error);

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

    private sealed record AttachmentSelfTestResult(
        bool Ok,
        bool PlaceholderResolved,
        bool AllFilesAccepted,
        bool ExtensionCountersOk,
        bool PromptManifestOk,
        bool ModelInspectionOk,
        bool StlInspectionOk,
        bool FbxInspectionOk,
        bool ComparisonOk,
        bool BinaryFallbackOk,
        bool ActiveDocumentUnchanged,
        bool TemporaryCleaned,
        bool DisposeCleanupOk,
        bool StaleCleanupOk,
        bool OriginalsPreserved,
        bool ImportToolOk,
        bool FbxImportToolOk,
        string ImportToolOutput,
        bool ImportStagingCleaned,
        bool ImportAlwaysPrompts,
        int ProviderPromptCount,
        IReadOnlyList<int> ImageCountsByRound,
        bool ImagesSentOnFirstRoundOnly,
        bool ClaudeStreamJsonOk,
        bool CodexInputOk,
        bool TurnSuccess,
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
        bool BackgroundIndexOk,
        bool NoUpdateOk,
        bool DisabledOk,
        string ExportPath,
        string? Error);

    private sealed class ScriptedAnnouncedActionProvider : IAgentProvider
    {
        private readonly string _marker;
        private int _turn;

        public ScriptedAnnouncedActionProvider(string marker)
        {
            _marker = marker;
        }

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted announced-action self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public List<string> Prompts { get; } = [];

        public Task<AgentProviderResult> RunPromptAsync(
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompts.Add(prompt.Text);
            _turn++;
            progress(new AgentProgress($"scripted announced-action self-test turn {_turn}"));

            var text = _turn == 1
                ? "I'll start modeling the Art Deco building with its main stepped massing."
                : BuildActionResponse();
            return Task.FromResult(new AgentProviderResult(
                text,
                "self-test",
                "scripted-announced-action",
                null,
                0,
                ""));
        }

        public void Reset()
        {
            _turn = 0;
            Prompts.Clear();
        }

        public void Dispose()
        {
        }

        private string BuildActionResponse()
        {
            var code = $$"""
                var attrs = new ObjectAttributes { Name = "RhinoAgent Announced Action Self-Test Box" };
                attrs.SetUserString("{{SelfTestMarkerKey}}", "{{_marker}}");
                var brep = new Box(
                    Plane.WorldXY,
                    new Interval(0, 10),
                    new Interval(0, 10),
                    new Interval(0, 10)).ToBrep();
                var id = doc.Objects.AddBrep(brep, attrs);
                doc.Views.Redraw();
                output.AppendLine($"Created announced-action recovery box: {id}");
                """;
            var envelope = new ToolCallEnvelope
            {
                ToolCalls =
                [
                    new ToolCallRequest
                    {
                        Tool = "execute_csharp",
                        Arguments = new Dictionary<string, object?> { ["code"] = code }
                    }
                ]
            };
            return $$"""
                Continuing now with the building's main massing.
                <rhino-agent>{{JsonSerializer.Serialize(envelope, JsonOptions.Loose)}}</rhino-agent>
                """;
        }
    }

    private sealed class ScriptedAttachmentProvider : IAgentProvider
    {
        private int _turn;

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted general-attachment self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public List<AgentProviderPrompt> Prompts { get; } = [];

        public Task<AgentProviderResult> RunPromptAsync(
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompts.Add(prompt);
            _turn++;
            progress(new AgentProgress($"scripted general-attachment self-test turn {_turn}"));

            var text = _turn == 1
                ? """
                  I will inspect the attached model using RhinoAgent's read-only attachment interpreter.
                  <rhino-agent>{"tool_calls":[{"tool":"inspect_attachment","arguments":{"attachment":"[.3dm 1]"}}]}</rhino-agent>
                  """
                : "The image used native provider input, the other files stayed local, and the attachment inspection follow-up completed.";
            return Task.FromResult(new AgentProviderResult(
                text,
                "self-test",
                "scripted-image-attachment",
                null,
                0,
                ""));
        }

        public void Reset()
        {
            _turn = 0;
            Prompts.Clear();
        }

        public void Dispose()
        {
        }
    }

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
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt.Text);
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
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt.Text);
            progress(new AgentProgress("scripted skill-use self-test turn"));

            var sawSkill = prompt.Text.Contains("--- skill: parametric-form-study ---", StringComparison.OrdinalIgnoreCase);
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
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _prompts.Add(prompt.Text);
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
                _ => ObserveCaptureAndRespond(prompt.Text)
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

    private sealed class ScriptedConversationProvider : IAgentProvider
    {
        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Scripted conversation self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;

        public Task<AgentProviderResult> RunPromptAsync(
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentProviderResult(
                "The background-indexing decision is recorded.",
                "self-test",
                "scripted-conversation",
                null,
                0,
                ""));
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingMemoryProvider : IAgentProvider
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _agentNotes;
        private readonly string _summary;

        public BlockingMemoryProvider(
            string agentNotes = "- Background conversation indexing completed.",
            string summary = "Background conversation indexing self-test completed.")
        {
            _agentNotes = agentNotes;
            _summary = summary;
        }

        public AgentProviderKind Kind => AgentProviderKind.Codex;
        public string DisplayName => "Blocking background-memory self-test provider";
        public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
        public Task Started => _started.Task;
        public bool IsReleased => _release.Task.IsCompleted;

        public async Task<AgentProviderResult> RunPromptAsync(
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var text = JsonSerializer.Serialize(new
            {
                update = true,
                agentNotes = _agentNotes,
                summary = _summary,
                reason = "Verified non-blocking background conversation indexing."
            });
            return new AgentProviderResult(text, "self-test", "background-memory", null, 0, "");
        }

        public void Release() => _release.TrySetResult(true);

        public void Reset()
        {
        }

        public void Dispose()
        {
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
            AgentProviderPrompt prompt,
            Action<AgentProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPrompt = prompt.Text;
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
