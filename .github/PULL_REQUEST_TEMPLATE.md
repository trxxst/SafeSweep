## What does this change?

<!-- A short description of the change and why it is needed. Link related issues, e.g. "Fixes #12". -->

## Type of change

- [ ] Bug fix
- [ ] New feature or cleaning category
- [ ] User interface or text
- [ ] Documentation, build or CI
- [ ] Change to what can be scanned, deleted or quarantined

## Safety checklist

Required when the last box above is ticked, or when the change touches `Safety/`, `Cleaning/`, `Scanning/` or `Rules/`.

- [ ] No protection rule was loosened; new behaviour is added with narrow, explicit rules.
- [ ] New cleaning categories start in Deep Clean or Manual Review unless the data is always recreated automatically.
- [ ] Every deletion still goes through `PathGuard` and `CleanEngine`.
- [ ] Tests show both the new behaviour and that neighbouring protected locations are still refused.
- [ ] New tests fail when the change is reverted.

## Checks

- [ ] `dotnet format whitespace SafeSweep.sln --verify-no-changes` passes
- [ ] `dotnet build SafeSweep.sln -c Release -warnaserror` passes
- [ ] `dotnet test SafeSweep.sln -c Release --filter "Category!=Integration"` passes
- [ ] Screenshots of UI changes are attached (with no private paths or user names)
- [ ] `CHANGELOG.md` is updated under "Unreleased" for user-visible changes
