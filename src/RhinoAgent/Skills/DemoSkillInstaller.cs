namespace RhinoAgent.Skills;

public static class DemoSkillInstaller
{
    public static IReadOnlyList<SkillOperationResult> Install(SkillStore store, bool overwrite)
    {
        return DemoSkills()
            .Select(skill =>
            {
                skill.Overwrite = overwrite;
                return store.SaveSkill(skill, update: false);
            })
            .ToArray();
    }

    private static IEnumerable<SkillWriteRequest> DemoSkills()
    {
        yield return new SkillWriteRequest
        {
            Name = "rhino-model-review",
            Description = "Review active Rhino documents for model organization, object counts, layers, bounding boxes, and visual sanity checks. Use when asked to inspect, audit, validate, or summarize Rhino model quality.",
            Files =
            [
                File("SKILL.md",
                    """
                    ---
                    name: rhino-model-review
                    description: Review active Rhino documents for model organization, object counts, layers, bounding boxes, and visual sanity checks. Use when asked to inspect, audit, validate, or summarize Rhino model quality.
                    ---

                    # Rhino Model Review

                    Start with `document_summary` and `list_objects` before making claims about the model.
                    Use `capture_viewport` only when visual framing, overlap, silhouette, or recognizability matters.
                    Report concrete object counts, layer names, units, and bounding-box observations.
                    Keep recommendations short and actionable.

                    For a more formal checklist, read `references/model-review-checklist.md`.
                    """),
                File("references/model-review-checklist.md",
                    """
                    # Model Review Checklist

                    - Confirm document units and tolerance-sensitive dimensions.
                    - Check whether objects are named and organized by layer.
                    - Look for extremely large or tiny bounding boxes that suggest bad scale.
                    - For generated geometry, verify silhouette with a viewport capture.
                    - Separate facts from visual impressions.
                    """)
            ]
        };

        yield return new SkillWriteRequest
        {
            Name = "parametric-form-study",
            Description = "Create quick RhinoCommon form studies with layers, named objects, simple parameters, and reusable code snippets. Use for massing, panel grids, facade studies, product mockups, or repeatable geometry generation.",
            Files =
            [
                File("SKILL.md",
                    """
                    ---
                    name: parametric-form-study
                    description: Create quick RhinoCommon form studies with layers, named objects, simple parameters, and reusable code snippets. Use for massing, panel grids, facade studies, product mockups, or repeatable geometry generation.
                    ---

                    # Parametric Form Study

                    Prefer `execute_csharp` for repeatable geometry because it can name objects, assign layers, and expose parameters in one script.
                    Build the main silhouette first, then add detail only after the base form exists.
                    Use clear layer names such as `Study::Primary`, `Study::Guides`, and `Study::Details`.
                    Print a compact parameter summary with `output.WriteLine(...)`.

                    Use `scripts/layered-box-grid.csx` as a starting pattern when making repeated modules.
                    """),
                File("scripts/layered-box-grid.csx",
                    """
                    var layerName = "Study::Primary";
                    var layer = doc.Layers.FindName(layerName);
                    var layerIndex = layer?.Index ?? doc.Layers.Add(layerName, System.Drawing.Color.CadetBlue);

                    for (var x = 0; x < 4; x++)
                    {
                        for (var y = 0; y < 3; y++)
                        {
                            var box = new Box(
                                Plane.WorldXY,
                                new Interval(x * 12, x * 12 + 8),
                                new Interval(y * 10, y * 10 + 6),
                                new Interval(0, 4 + x + y)).ToBrep();
                            var attrs = new ObjectAttributes { Name = $"module_{x}_{y}", LayerIndex = layerIndex };
                            doc.Objects.AddBrep(box, attrs);
                        }
                    }

                    doc.Views.Redraw();
                    output.WriteLine("Created a layered 4 x 3 box grid study.");
                    """),
                File("assets/palette.txt",
                    """
                    Primary: CadetBlue
                    Guides: LightGray
                    Details: Coral
                    """)
            ]
        };

        yield return new SkillWriteRequest
        {
            Name = "skill-writer",
            Description = "Create, revise, package, and validate Codex-style RhinoAgent skills with SKILL.md frontmatter, progressive disclosure, optional references, scripts, assets, examples, and safe create_skill manifests. Use when the user asks RhinoAgent to save a workflow, make a skill, improve an existing skill, create reusable agent instructions, package domain knowledge, or turn repeated Rhino/modeling/file workflows into a reusable skill.",
            Files =
            [
                File("SKILL.md",
                    """
                    ---
                    name: skill-writer
                    description: Create, revise, package, and validate Codex-style RhinoAgent skills with SKILL.md frontmatter, progressive disclosure, optional references, scripts, assets, examples, and safe create_skill manifests. Use when the user asks RhinoAgent to save a workflow, make a skill, improve an existing skill, create reusable agent instructions, package domain knowledge, or turn repeated Rhino/modeling/file workflows into a reusable skill.
                    ---

                    # Skill Writer

                    Use this skill to create or improve RhinoAgent skills that are useful to another agent later. Treat a skill as a compact onboarding guide plus optional bundled resources, not as ordinary documentation.

                    ## Core Rules

                    - Ground the skill in concrete use cases before writing files.
                    - Put trigger guidance in the `description` frontmatter because RhinoAgent matches skills from metadata before loading the body.
                    - Keep `SKILL.md` concise and procedural; move long details into one-level `references/` files.
                    - Include `scripts/` only when repeated code should be reused or determinism matters.
                    - Include `assets/` only for templates, starter files, examples, or binary resources the agent should copy or adapt.
                    - Do not create README, changelog, installation guide, or other auxiliary docs inside a skill unless the user explicitly asks.
                    - Use lowercase hyphenated names, under 64 characters, matching the folder and frontmatter `name`.
                    - Use `create_skill` or `update_skill`; do not use `write_file` to bypass RhinoAgent skill validation and approval.

                    ## Workflow

                    1. Clarify only the missing decisions that materially change the skill. Prefer deriving facts from the current Rhino document, files, or prior workflow.
                    2. Identify concrete example prompts that should trigger the skill.
                    3. Decide what belongs in `SKILL.md` versus `references/`, `scripts/`, and `assets/`.
                    4. Draft a complete file manifest for `create_skill` or `update_skill`.
                    5. Validate mentally before calling the tool: name, description, frontmatter, path safety, resource usefulness, and no unnecessary files.
                    6. Call `create_skill` with `overwrite=false` for a new skill. Use `update_skill` only when revising an existing skill.
                    7. After tool success, tell the user the skill name, saved files, and one command they can use to test it.

                    ## RhinoAgent-Specific Guidance

                    - RhinoAgent skills can be general-purpose, but they can only rely on capabilities available through RhinoAgent tools.
                    - For Rhino modeling skills, prefer instructions that combine `document_summary`, `list_objects`, `execute_csharp`, `run_command`, and `capture_viewport`.
                    - For skill resources that need more context, tell the future agent exactly when to call `read_skill_file`.
                    - Treat generated scripts as reusable snippets unless the user explicitly asks for a directly executable workflow.
                    - Skill creation persists under the user RhinoAgent skill root and will require approval through the `create_skill` manifest preview.

                    ## Read When Needed

                    - For skill folder anatomy and frontmatter rules, read `references/anatomy.md`.
                    - For choosing references, scripts, and assets, read `references/resource-design.md`.
                    - For RhinoAgent-specific tool and safety constraints, read `references/rhinoagent-skill-safety.md`.
                    - For final checks before calling `create_skill`, read `references/validation-checklist.md`.
                    """),
                File("references/frontmatter-template.md",
                    """
                    ---
                    name: short-hyphenated-name
                    description: State what the skill does and when to use it, including trigger phrases, task types, file types, tools, or domain contexts that should activate it.
                    ---

                    # Short Title

                    Start with the minimum workflow the agent must follow.
                    Link optional references only when needed.
                    Mention scripts and assets only when they are bundled and useful.
                    """),
                File("references/anatomy.md",
                    """
                    # Skill Anatomy

                    A RhinoAgent skill is a folder with a required `SKILL.md` and optional bundled resources.

                    ```text
                    skill-name/
                      SKILL.md
                      references/
                      scripts/
                      assets/
                      agents/
                        openai.yaml
                    ```

                    ## Required SKILL.md

                    Use YAML frontmatter with only the fields RhinoAgent needs:

                    ```markdown
                    ---
                    name: skill-name
                    description: What the skill does. Use when ...
                    ---
                    ```

                    The `name` must be lowercase letters, digits, and hyphens. The `description` must include both capability and trigger conditions because it is the main matching surface before the body is loaded.

                    ## Body

                    Write in imperative, procedural language. Assume the future agent is capable, but lacks this workflow-specific knowledge.

                    Good body content:

                    - Ordering constraints.
                    - Tool preferences.
                    - Domain-specific gotchas.
                    - Links to optional reference files.
                    - Validation steps.

                    Avoid:

                    - Generic explanations any model already knows.
                    - Long background essays.
                    - User-facing install notes.
                    - Duplicating the same detail in both `SKILL.md` and references.

                    ## Optional agents/openai.yaml

                    Create this only if a UI needs display metadata. RhinoAgent does not require it for matching or loading.
                    """),
                File("references/resource-design.md",
                    """
                    # Resource Design

                    Use progressive disclosure: keep the always-loaded `SKILL.md` small, then add resources only when they improve reuse.

                    ## references/

                    Use references for detailed knowledge the future agent should load only when needed.

                    Good examples:

                    - Rhino modeling checklist.
                    - Company-specific naming rules.
                    - API schema.
                    - File format gotchas.
                    - Long examples.

                    Keep references one level below `SKILL.md`. If a reference is long, put a short table of contents near the top.

                    ## scripts/

                    Use scripts when code would otherwise be rewritten repeatedly or when deterministic behavior matters.

                    Good examples:

                    - RhinoCommon C# snippet for a repeated geometry pattern.
                    - Python validation helper.
                    - Data cleanup script.
                    - Export/conversion helper.

                    For RhinoAgent, scripts are usually snippets the agent reads or adapts, unless the workflow explicitly calls for executing a file through an approved tool.

                    ## assets/

                    Use assets for reusable templates or source material.

                    Good examples:

                    - Starter `.3dm` or template files.
                    - Style palettes.
                    - CSV templates.
                    - Example prompts.
                    - Static reference images.

                    Do not place large assets unless the user asked for them or they are necessary for the skill.

                    ## Choosing Resources

                    Ask this for each proposed file:

                    - Would the future agent need this more than once?
                    - Is it too detailed for the always-loaded body?
                    - Does it remove fragile rewriting?
                    - Is it directly tied to the skill's promise?

                    If the answer is no, leave it out.
                    """),
                File("references/rhinoagent-skill-safety.md",
                    """
                    # RhinoAgent Skill Safety

                    RhinoAgent skills run inside a constrained agent loop. The provider should not call native shell, MCP, web, or app-server tools directly. Skills should guide the agent toward RhinoAgent hidden tools.

                    ## Preferred Tool Use

                    - Use `document_summary` for document units, object counts, layers, and bounding boxes.
                    - Use `list_objects` for object IDs, names, layers, types, and per-object bounding boxes.
                    - Use `execute_csharp` for bounded RhinoCommon geometry creation or inspection.
                    - Use `run_command` for complete Rhino command macros with prompts answered inline.
                    - Use `capture_viewport` for visual validation after factual inspection.
                    - Use `fetch_url` only when the user asks from an HTTP or HTTPS source.
                    - Use `read_skill_file` for optional skill references and snippets.
                    - Use `create_skill` and `update_skill` for skill persistence.

                    ## Skill Creation Constraints

                    - Do not use absolute paths inside skill manifests.
                    - Do not write outside the skill folder.
                    - Do not include secrets, account tokens, or private machine-specific paths unless the user explicitly asks and the skill is private.
                    - Do not promise capabilities RhinoAgent does not expose.
                    - Treat `scripts/` as reusable resources, not automatically trusted executables.
                    - Keep generated code bounded and testable through normal RhinoAgent approval modes.

                    ## Approval Expectations

                    `create_skill`, `update_skill`, `delete_skill`, and `export_skill` show a manifest and ask for approval. A good skill creation response should make the approval easy to understand:

                    - Name.
                    - Purpose.
                    - Files to write.
                    - Why each resource exists.
                    - Suggested first test prompt.
                    """),
                File("references/validation-checklist.md",
                    """
                    # Validation Checklist

                    Before calling `create_skill` or `update_skill`, verify:

                    - The name is lowercase hyphen-case and under 64 characters.
                    - `SKILL.md` frontmatter contains `name` and `description`.
                    - The frontmatter name matches the requested skill name.
                    - The description includes trigger conditions, not just a summary.
                    - The body gives an actionable workflow.
                    - Optional references are linked from `SKILL.md` with clear load conditions.
                    - Scripts/assets are justified by reuse or determinism.
                    - The manifest contains only relative paths inside the skill folder.
                    - No placeholder files remain.
                    - No README, changelog, or install guide is included by default.
                    - The skill does not rely on unavailable native provider tools.
                    - The final answer tells the user how to test the skill with `/skill show` or `/skill use`.

                    For a new skill, call:

                    ```json
                    {
                      "tool": "create_skill",
                      "arguments": {
                        "name": "skill-name",
                        "description": "Capability and trigger conditions.",
                        "overwrite": false,
                        "files": [
                          { "path": "SKILL.md", "content": "..." }
                        ]
                      }
                    }
                    ```
                    """)
            ]
        };
    }

    private static SkillFileSpec File(string path, string content) =>
        new()
        {
            Path = path,
            Content = content
        };
}
