# CodeBox X Sample Extensions

CodeBox X seeds the local Marketplace catalog on first launch with three **data-only** `.cbxext` packages. The package definitions are kept in `Services/ExtensionMarketplaceService.cs` so their integrity hash is calculated from the exact serialized manifest and payload before they are written to the user's local catalog.

| Extension ID | Package type | Demonstrates |
|---|---|---|
| `codebox.midnight-aurora` | Syntax theme | Window, editor, sidebar, terminal, and syntax colors with preview, apply, and reset support. |
| `codebox.clean-code-linter` | Linter | `TODO` and trailing-whitespace diagnostics with severity, line, column, highlighting, and Problems panel output. |
| `codebox.focus-tools` | Productivity / editor tool | A lightweight focus-note template and an available version update workflow. |

## `.cbxext` package contract

A package is JSON data rather than executable code. It contains an `ExtensionManifest`, an optional `ThemeDefinition`, optional `LinterRule` entries, and optional productivity metadata. CodeBox X verifies the following before installation:

1. The file has a `.cbxext` extension and is below the 1 MB limit.
2. The JSON schema version, identifier, version, types, and required metadata are valid.
3. All permissions belong to the allowlist: `editor.theme`, `editor.diagnostics`, or `editor.productivity`.
4. Theme and linter payloads include their mandatory definitions.
5. The SHA-256 `PackageHash` matches the package payload after the hash field is cleared.

> The current marketplace intentionally never loads assemblies, scripts, or executable payloads. The `MarketplaceApi` and `IMarketplaceCatalogSource` abstractions allow a future authenticated server catalog to be connected without changing this validation and installation path.
