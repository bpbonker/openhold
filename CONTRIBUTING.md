# Contributing to Openhold

Openhold is open source under the [Apache License 2.0](LICENSE). Bug reports, ideas, documentation and
code are all welcome.

## Licence of contributions

Contributions are made under the [Contributor Licence Agreement](CLA.md). You keep the copyright
in your own work and license it to the project, so Openhold can stay under one consistent licence.

## Sign your commits

Every commit must carry a `Signed-off-by` line. It certifies the
[Developer Certificate of Origin](https://developercertificate.org/) (that you wrote the change, or
otherwise have the right to submit it) and confirms that you agree to the [CLA](CLA.md).

```sh
git commit -s -m "Describe the change"
```

## Before you open a pull request

- `dotnet build` passes with no warnings (warnings are errors) and `dotnet test` passes.
- New behaviour has a test.
- No company data, credentials, customer files or Autodesk binaries in the change.
- Examples in code and docs use neutral names (for example `PRJ01`), not a real company's.
