# DeathWish

This repository contains the DeathWish game server. The Unity client is developed separately.

Apply these C# conventions to the server and its protocol code.

## Implementation Workflow

- Follow [the implementation plan](docs/ImplementationPlan.md) for the staged server work.
- Implement one stage at a time and verify its review criteria.
- Stop after each stage for the user's code review. Address review feedback before proceeding.
- Mark a stage complete after the user accepts its review. Continue according to the user's latest instructions, including any explicit authorization to proceed through multiple stages.
- Report changed files, verification results, and remaining limitations at each review checkpoint.
- Keep the plan updated as implementation decisions and scope change.

## Comments

- Write all code comments in English, including XML documentation comments and TODOs.
- Use descriptive names and clear control flow. Omit comments that repeat what the code does.
- Place a brief `//` comment immediately above the relevant code when a maintainer needs to understand a non-obvious constraint or decision. Examples include tick ordering, thread ownership, buffer lifetimes, and protocol compatibility.
- Use `///` for caller-facing contracts that need to explain units, valid ranges, failure behavior, or ownership.
- Keep wire layouts, byte order, and quantization rules in the shared protocol specification. Update comments and documentation when the behavior they describe changes.

## Blank Lines

- Keep statements that serve the same purpose together.
- Use one blank line between distinct steps inside a method, such as validating an input and enqueueing a world command.
- Keep related declarations, field reads, and checks together. Add spacing where the purpose changes.
