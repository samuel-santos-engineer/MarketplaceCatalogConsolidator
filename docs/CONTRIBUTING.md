# Contributing

Use this workflow for every project change:

```text
feature branch -> pull request -> user review -> user merge to main
```

Create a focused feature branch from the current `main`, make the change, and run the repository verifier and secret scanner. Open a PR into `main` using the template and include verification evidence and any migration/data impact. The repository owner reviews and performs every merge. Do not push project changes directly to `main`, merge a PR on the owner's behalf, or enable auto-merge.

Required local checks:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
gitleaks dir . --config .gitleaks.toml --redact --no-banner
```

On Linux, use `sh scripts/verify.sh`. CI parses its workflow as part of the tests, builds Release, runs all tests, verifies formatting, and scans both the checkout and Git history with Gitleaks. New tests must use disposable database/storage roots and preserve the starter `artifacts/catalog.db`. Runtime data, local settings, keys, and generated output must never be committed.

Keep changes within the agreed milestone. Docker image work and Azure deployment are reserved for the final deployment milestone. Business policy, matching, immutable reports, and migrations need explicit review when changed. Update the README and API documentation when behavior changes.
