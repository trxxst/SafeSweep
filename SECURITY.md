# Security policy

SafeSweep deletes files, so for this project "security problem" includes more than the
usual vulnerabilities. Please report privately:

- **Dangerous deletion behaviour**: any way to make SafeSweep delete, quarantine or offer
  something it should protect (system files, program folders, personal files, browser
  passwords or cookies, cloud-synced files, other users' data), for example through
  crafted paths, junctions, symbolic links, hard links, unusual file names, race conditions
  or settings.
- **Data loss in the quarantine**: an item that cannot be restored, is restored to the wrong
  place, or overwrites something on restore.
- **Privilege problems**: anything that lets a standard user make SafeSweep act with
  administrator rights, or that lets another user read or change your quarantine.
- **Installer and update problems**: for example a way to replace files in the install
  folder or to make the scheduled task run something else.
- Ordinary vulnerabilities in SafeSweep's code or its build and release process.

## Supported versions

Only the latest release receives fixes. Please check that the problem still exists in the
[latest version](https://github.com/trxxst/SafeSweep/releases/latest) before reporting.

## How to report

Use GitHub's private reporting: open the
[Security tab of the repository](https://github.com/trxxst/SafeSweep/security) and choose
**Report a vulnerability**. This creates a private advisory that only the maintainers can
see.

If that option is not available, open a normal issue that says only that you have a
security report and asks for a private contact. **Do not put details in a public issue.**

Please include:

- the SafeSweep version (shown at the bottom of the sidebar) and your Windows version,
- step-by-step instructions to reproduce the problem, ideally with a folder layout that can
  be created in a test folder,
- what SafeSweep did and what you expected it to do.

Remove private information before sending: SafeSweep's logs and result lists contain full
file paths, which usually include your Windows user name.

## What happens next

You should get a first answer within 7 days. Confirmed problems that can cause data loss are
fixed before anything else, released as soon as possible and credited in the release notes
(unless you prefer not to be named). Please give us a reasonable time to release a fix before
you publish details.

## Safe testing

Please test with files you can afford to lose, inside a test folder or a virtual machine,
and turn on **Simulation mode** in Settings while you explore. Do not test against other
people's computers or data.
