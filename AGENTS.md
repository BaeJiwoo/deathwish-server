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

## Daily Development Log

- Maintain dated development logs in `docs/devlog/YYYY-MM-DD.md` using Asia/Seoul dates.
- After implementation, fixes, or verification, update the current day's log before the review handoff or final response. Continue the same file for additional work on that date.
- Follow `docs/devlog/README.md` and add new dates to its index.
- Record actual changes, design decisions, verification results, limitations, and the next starting point. Distinguish implemented work from proposals and deferred work.
- Include commit hashes only after checking Git history; do not infer review acceptance, commits, or pushes from implementation alone. Do not commit solely to update the log.
- Never copy secrets or private `.env` values into logs.

## Constants

- Name all `const` fields and local constants in `UPPER_SNAKE_CASE`: uppercase letters with underscores between words, such as `HEADER_SIZE`, `MAX_PAYLOAD_LENGTH`, and `DEFAULT_TICK_RATE`.
- Apply this convention regardless of accessibility. Keep digits where needed; do not use camelCase or PascalCase for constants.
- Replace meaningful magic numbers, such as protocol sizes and processing limits, with named constants. Do not introduce constants for self-evident values without a domain meaning.

## Naming and Types

- Include units in names for numeric quantities whose units would otherwise be ambiguous, such as `timeoutMs`, `durationSeconds`, and `MAX_PENDING_BYTES`.
- Name booleans as readable conditions using prefixes such as `Is`, `Has`, and `Can` (for example, `IsConnected`, `HasPendingWrite`, and `CanJoin`). Follow the casing appropriate to the member or local variable.
- Use `var` when the type is clear from the right-hand side, such as `var sessions = new SessionManager();`. Otherwise, use an explicit type when it improves readability.

## Control Flow and Formatting

- Prefer early returns for invalid input and terminal states to reduce nesting and keep the normal path clear.
- Use braces for conditional and loop bodies even when they contain only one statement. Put opening and closing braces on separate lines.
- Write separate operations on separate lines. Do not compress state changes, method calls, returns, or `lock`, `try`, and `catch` bodies into a single line.
- Give complex conditions meaningful names through a local boolean or a focused method, such as `isOutputLimitExceeded`. Keep simple conditions inline when they are already clear.

## Class and Method Organization

- Arrange class members consistently: constants, fields, properties, constructors, then methods. Keep related methods together.
- Give each method one coherent responsibility. Separate validation, queueing, and transmission when they represent distinct responsibilities.
- Keep code serving the same purpose together. Extract methods at responsibility boundaries rather than enforcing an arbitrary line-count limit.

## Thread and Buffer Ownership

- Document caller-visible thread restrictions and buffer ownership in method contracts using `///`, including when only the world loop may call a method and when ownership transfers to the callee or transport.
- Use a brief English `//` comment above internal code when ordering, synchronization, or buffer lifetime constraints are not apparent from the code. Do not repeat an existing contract without adding useful context.

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
