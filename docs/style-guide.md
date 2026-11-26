# Style guide

Rules for every change, human or agent. Structural rules are enforced by `CodeStyleTests` (`tests/RnRomManager.Api.Tests/CodeStyle`); never add to its baseline: remove lines you fix, and rename a line's path only when you move that file. Where to make common changes: [recipes.md](recipes.md).

## General
- Smallest complete change; reuse an existing helper or component before writing one (grep `docs/codemap.md`). Duplicate logic twice → extract it.
- Keep files under ~500 lines (guideline; test files exempt). Split by responsibility, not by region.
- Comments explain why, not what. Cite the spec rule a behaviour comes from (`/** B12: … */`).
- No behaviour change hides in a refactor; every behaviour change gets tests and its doc in the same PR ([CONTRIBUTING.md](../CONTRIBUTING.md)).
- Never commit secrets or dev passwords.

## C#
- One type per file (class, record, struct, interface, enum, delegate), named after the type; no nested types. Only `Foo` and `Foo<T>` may share a file.
- File-scoped namespace matching the folder; `using`s outside it, `System` first (`.editorconfig`).
- 4+ parameters in a declaration (constructor, primary constructor, record, method, local function, delegate): one per line, indented one level, `)` on the last parameter's line:
  `public sealed class IngestJob(` / `    DatCache dats,` / … / `    ILogger<IngestJob> logger) : IJob`
- Classes are `sealed` unless designed for inheritance; DI services use primary constructors.
- Nullable is on and warnings are errors: avoid the `!` operator in new code (test fields `= null!` are fine).
- Async methods end in `Async` and take a `CancellationToken` last.
- Private fields `_camelCase`; constants and static readonly `PascalCase`.
- Interfaces live in Core, Dapper implementations in `Data/Repositories`; use `QueryPageAsync` for paged lists.
- API errors are ProblemDetails via `Problems` (`Validation`, `NotFound`, `Forbidden`, `Create`); a busy job lock is `JobStart.Running(ctx)`; start manual jobs with `JobStart.StartManualAsync`.
- System handlers load resources with `EmbeddedResource`/`BoxArt` and decode header text with `HeaderText`.
- Tests: NUnit `Assert.That` + NSubstitute, `<Class>Tests` in the mirrored folder, `TempDir` for files, `TestHandlers.Fake` for handlers, `[Category("Integration")]` for Testcontainers.

## Svelte / TypeScript
- Svelte 5 runes: `interface Props` + `$props()`, `$state`, `$derived`, `$bindable` for two-way props.
- Prettier: tabs, single quotes, width 100. `npm run lint` and `npm run check` must pass.
- Imports: `#lib/…` (TS modules with `.js`), or `./` within one folder; never `../` into `src/lib` (files outside it, such as `app.css` or `static/…`, keep their relative path).
- Layout of `src/lib`: `types/` and `enums/` (one exported type per file, re-exported by `index.ts`; enums are string-literal unions), `icons/` (`Icon` + `icons.ts`), `stores/` (`*.svelte.ts`), `components/<folder>/`: `modals`, `layout`, `forms`, `common`, `games`, `discovery`, `systems`, `bundles`, `ingest`, `review`. Exported types live nowhere else.
- Components stay under ~500 lines; extract sections into the feature folder.
- Modals: every modal is `components/modals/<Name>Modal.svelte` built on `BaseModal` (`GlobalSearch` keeps its name and markup). Only `ConfirmModal` (title + plain-text message) is used inline.
- Forms: use `components/forms` (`FormField`, `TextInput`, `SearchInput`, `NumberInput`, `FileInput`, `Textarea`, `Select`, `Checkbox`, `Switch`, `RadioButtons`, `Button`); no raw `<input>`, `<select>`, `<textarea>` or `<button>` elsewhere. Single radios are `Checkbox type="radio"`; button-style radio groups are `RadioButtons`. Every control has a label (`hiddenLabel` for visually hidden) or an `aria-label`.
- Reuse: `DataTable` for carded tables, `SortHeader` for sortable columns, `NavTabs` for tab strips, `Pagination`, `ViewToggle`, `ErrorAlert`, `EmptyState`, `Loading`, `FileSize` for sizes, `formatCount` for numbers, `AdminOnly` for gating, the `toasts` store, `lib/sort.ts`, `lib/urlState.ts`, `lib/debounce.ts`, `useAbortableGet`.
- Styling: Bootstrap utilities first; component styles in the component's `<style>`; `src/app.css` only for theme tokens, Bootstrap overrides, system colours and rules shared by 2+ components; no static `style="…"` (dynamic values only).
- Icons through `<Icon name>` only; Font Awesome for brands only.
- Tests sit next to their source as `*.test.ts` (rune stores `*.svelte.test.ts`), vitest; shared mocks in `lib/testing/setup.ts`.
