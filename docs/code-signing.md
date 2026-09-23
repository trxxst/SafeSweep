# Code signing

SafeSweep releases are currently **not signed**. Windows SmartScreen therefore shows a
warning the first time a download is run, until the file has built up reputation. The
README and release notes explain this to users and show how to verify the SHA-256
checksums published with every release.

Nothing in the build pretends otherwise: there is no self-signed or test certificate, and
the build does not try to bypass or suppress SmartScreen.

## Adding signing later

When a certificate is available (an OV/EV code-signing certificate, or a cloud signing
service such as Azure Trusted Signing or SignPath for open-source projects), sign in this
order, because each step packages the output of the previous one:

1. **The application**, after `dotnet publish` in `scripts/build-release.ps1`:
   `artifacts\app\SafeSweep.exe` and `artifacts\portable\SafeSweep.exe`, before they are
   copied to `dist\` and before the MSI is built.
2. **The MSI**, `artifacts\msi\SafeSweep-x64.msi`, before the setup program is built.
3. **The setup program.** A WiX bundle carries an embedded engine that must be signed
   separately. The WiX SDK supports this through MSBuild targets: set
   `<SignOutput>true</SignOutput>` in `installer/bundle/SafeSweep.Bundle.wixproj` and
   implement the `SignBundleEngine` and `SignBundle` targets (and `SignMsi` in
   `installer/msi/SafeSweep.Installer.wixproj`) to call your signing tool on the files
   passed to them. See the WiX documentation on signing for details.

With `signtool` a signing call looks like this (use a timestamp server so signatures stay
valid after the certificate expires):

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <certificate thumbprint> <file>
```

In GitHub Actions, keep the certificate or signing credentials in repository secrets and
only sign on tag builds. Never commit certificates, private keys or passwords; `*.pfx`,
`*.p12`, `*.key` and `*.pem` are ignored by `.gitignore` for that reason.

After signing, verify with `signtool verify /pa /v <file>` and check that
`Get-AuthenticodeSignature <file>` reports `Valid`.
