# Contributing to SemanticSourceCode

Thank you for your interest in contributing! This document provides guidelines to help you get started.

## How to Contribute

1. **Fork the repository** and create your branch from `main`
2. **Make your changes** — keep them focused and well-scoped
3. **Add or update tests** for any new functionality
4. **Ensure the build passes** and all tests are green
5. **Submit a pull request** with a clear description

## Bug Reports

Before opening a bug report, please:

- Check if the issue already exists
- Include reproduction steps, expected behavior, and actual behavior
- Provide your environment details (OS, .NET version, provider used)
- Attach relevant logs or error messages

## Feature Requests

Feature requests are welcome! Please:

- Describe the problem you're trying to solve
- Explain your proposed solution
- Consider alternative approaches
- Be open to discussion and iteration

## Pull Requests

### Requirements

- One logical change per PR
- All existing tests must pass (`dotnet test`)
- Build must succeed (`dotnet build`)
- Code should follow the existing style

### Code Style

This project uses standard C# conventions. Please:

- Follow the existing code style in the repository
- Use meaningful variable and method names
- Keep methods focused and reasonably sized
- Add XML documentation for public APIs

> **Note:** An `.editorconfig` file is not yet present in this repository. If you add one, please include it in a separate PR.

## Testing

We use **xUnit** for testing. Before submitting:

```bash
dotnet build
dotnet test
```

All tests must pass. If you add new functionality, add corresponding tests in the `SemanticSourceCode.Tests` project.

## Development Setup

```bash
# Clone your fork
git clone https://github.com/YOUR_USERNAME/SemanticSourceCode.git
cd SemanticSourceCode

# Build
dotnet build

# Run tests
dotnet test

# Run the tool
dotnet run -- --mode search
```

## Questions?

Feel free to open an issue or start a discussion. We're happy to help!
