# Revenant Workspace Warden

A very powerful code checker / linter, plus an expert tutor in the field of AI Engineering. Trained on the Maestro.org syllabus for the AAS in AI Engineering course.

Revenant Workspace Warden provides automated, configurable code checks and an AI-driven tutor designed to help engineers learn and apply AI Engineering best practices while enforcing code quality across C# (.NET) projects.

- Language: C#
- Repository: Revenant-Systems-LLC/Revenant-Workspace-Warden
- Purpose: Static analysis, linting, and interactive tutoring for AI Engineering projects

---

## Table of contents

- [Key features](#key-features)
- [Quick start](#quick-start)
- [Installation](#installation)
- [Usage examples](#usage-examples)
- [Configuration](#configuration)
- [Rules and extensibility](#rules-and-extensibility)
- [Integrations & CI](#integrations--ci)
- [Development setup](#development-setup)
- [Testing](#testing)
- [Contributing](#contributing)
- [Roadmap](#roadmap)
- [License](#license)
- [Acknowledgements & Contacts](#acknowledgements--contacts)

---

## Key features

- Static analysis and linting tailored for AI/ML projects (data pipelines, model code, experiment reproducibility).
- Configurable rule sets and severity levels.
- Auto-fix suggestions for common problems where safe.
- Context-aware suggestions and explanations (tutor mode) trained on Maestro.org AAS in AI Engineering syllabus.
- CLI and library modes: run as a tool in CI, or import into other tooling.
- Extensible rule/plugin system for custom checks (C# / Roslyn analyzers).
- Output formats: human-readable console, JSON, SARIF for CI integration.

---

## Quick start

1. Clone the repository:
   git clone https://github.com/Revenant-Systems-LLC/Revenant-Workspace-Warden.git
2. Build:
   dotnet build
3. Run the linter (example — adapt to the actual binary/project in this repo):
   dotnet run --project src/Warden.Cli -- lint ./path/to/project --format console
4. Run the interactive tutor for a topic:
   dotnet run --project src/Warden.Cli -- tutor "model evaluation"

Note: Replace `src/Warden.Cli` and command names with the actual project and binary names in the repository if they differ.

---

## Installation

Revenant Workspace Warden can be used in one of several ways depending on how the repository packages the tool:

- As a dotnet global tool (recommended if published):
  dotnet tool install -g revenant-warden --version x.y.z
  revenant-warden lint ./myproject

- From source (development):
  dotnet build
  dotnet run --project <CLI_PROJECT> -- <command>

- As a library:
  Reference the core package/project in your solution and call the analyzer APIs directly.

Prerequisites
- .NET SDK (check the repository's project files for the exact target framework; common frameworks include net6.0/net7.0/net8.0).
- A modern editor (Visual Studio / Rider / VS Code) for development and rule authoring.

---

## Usage examples

Lint a directory (console output):
  revenant-warden lint ./src --output console

Lint and produce SARIF for CI:
  revenant-warden lint ./src --output sarif --out ./reports/warden.sarif

Attempt auto-fixes where available:
  revenant-warden lint ./src --fix

Tutor: get an explanation and learning exercise about a topic:
  revenant-warden tutor "dataset versioning" --level intermediate

Run a single rule:
  revenant-warden lint ./src --rule "no-hardcoded-credentials"

Export results as JSON:
  revenant-warden lint ./src --output json --out ./reports/results.json

(Replace `revenant-warden` with the actual CLI name if different.)

---

## Configuration

Warden is driven by a configuration file (example `warden.json` or `.wardenrc` — update names to match the repo). Sample config:

```json
{
  "version": 1,
  "rules": {
    "no-hardcoded-credentials": { "enabled": true, "severity": "error" },
    "use-evaluation-metrics": { "enabled": true, "severity": "warning" },
    "experiment-reproducibility": { "enabled": true, "severity": "warning" }
  },
  "ignore": [
    "**/third_party/**"
  ],
  "tutor": {
    "enabled": true,
    "syllabus": "maestro-aas-ai-engineering",
    "default_level": "beginner",
    "allow_exercises": true
  },
  "output": {
    "format": "console",
    "path": "./reports/warden-report.json"
  }
}
```

Configuration notes:
- `rules` controls enabled/disabled rules and their severity.
- `ignore` uses glob patterns.
- `tutor.syllabus` points to the syllabus used for tutor suggestions (default: Maestro AAS).
- `output.format` supports `console`, `json`, `sarif`.

---

## Rules and extensibility

Warden’s checks are implemented as modular rules (Roslyn analyzers or a plugin interface). You can:

- Enable/disable rules in `warden.json`.
- Implement custom rules by creating new analyzer classes (C#) and registering them as plugins.
- Ship rule packs for domain-specific checks.

Example skeleton for a custom C# rule (Roslyn-style):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class NoHardcodedCredentialsAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: "W001",
        title: "Avoid hardcoded credentials",
        messageFormat: "Hardcoded credential found: '{0}'",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeNode, SyntaxKind.StringLiteralExpression);
    }

    private void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        // Implementation: detect suspicious string literals
    }
}
```

---

## Integrations & CI

Example GitHub Actions snippet to run Warden on push and PRs and upload SARIF:

```yaml
name: Warden Lint

on: [push, pull_request]

jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Restore & Build
        run: dotnet build --no-restore
      - name: Run Warden
        run: dotnet run --project src/Warden.Cli -- lint ./ --output sarif --out ./reports/warden.sarif
      - name: Upload SARIF
        uses: github/codeql-action/upload-sarif@v2
        with:
          sarif_file: reports/warden.sarif
```

Adjust the `dotnet-version`, project path, and commands to match the repository structure.

---

## Development setup

1. Clone and open the solution in your IDE:
   git clone https://github.com/Revenant-Systems-LLC/Revenant-Workspace-Warden.git
   cd Revenant-Workspace-Warden
2. Restore:
   dotnet restore
3. Build:
   dotnet build
4. Run CLI locally:
   dotnet run --project src/Warden.Cli -- help
5. Adding a rule:
   - Add a new analyzer project under `src/rules/YourRule`
   - Implement the rule and unit tests
   - Register the rule in the rule manager or plugin manifest

Coding standards
- Follow the repository's .editorconfig/.stylecop rules (if present).
- Keep changes small and well-documented; include unit tests for new checks.

---

## Testing

Run unit and integration tests:
  dotnet test

When adding rules:
- Include unit tests that target both positive (violation detected) and negative (no violation) cases.
- For tutor functionality, include tests for expected prompt outputs and exercise generation.

---

## Contributing

We welcome contributions!

- Fork the repo and create a feature branch: `feature/your-feature-name`
- Write tests and ensure `dotnet test` passes
- Keep commits focused and descriptive
- Open a pull request against `main` (or the project's default branch)
- Include a concise PR description and link to related issues

Please follow the [Code of Conduct](CODE_OF_CONDUCT.md) if present. If not present, consider adding one.

---

## Roadmap

Planned improvements (examples — update to reflect actual project plans):

- Publish a dotnet global tool / NuGet package
- Web UI for viewing reported issues and interactive tutor sessions
- More rule packs (security, MLOps practices, performance)
- Integration with IDEs (VS Code extension, Rider/Visual Studio)
- Fine-grained user analytics and learning progress for tutor mode (opt-in)

If you maintain a project board or milestones, link them here.

---

## License

This repository does not currently contain a license file. Please add a LICENSE file at the repository root and update this section accordingly. Common choices: MIT, Apache-2.0.

---

## Acknowledgements & contacts

- Trained on the Maestro.org AAS in AI Engineering syllabus — thanks to Maestro for the curriculum.
- Maintainers: Revenant Systems LLC
- For support, issues, or feature requests: open an issue on GitHub or contact the maintainers via the repository.

---

## Appendix: Example rule config and sample output

Sample `warden.json` rule snippet:

```json
{
  "rules": {
    "W001-no-hardcoded-credentials": { "enabled": true, "severity": "error" },
    "W010-experiment-not-deterministic": { "enabled": true, "severity": "warning" }
  }
}
```

Sample console output:

```
W001  error  Secrets found in file ./src/Models/Secrets.cs: line 12
W010  warning  Random seed not set in ./src/Training/Trainer.cs: line 45
2 problems found (1 error, 1 warning)
```

---

If you'd like, I can:
- Commit this README.md to the repository and open a PR,
- Modify the README to include exact command paths and .NET target frameworks after I inspect the repository files,
- Or generate a GitHub Actions workflow file to run the linter automatically.

Which would you prefer next?
