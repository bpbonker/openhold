# Third-party notices

Openhold uses the open-source packages below. Each remains under its own licence; the full text of each is in its
package (or at the link). `tools/security-check.ps1` writes the complete list, including indirect dependencies, to
`data/sbom.json`. Check it against this file before each release.

## Shipped with Openhold

| Package | Used for | Licence |
|---|---|---|
| [Npgsql](https://github.com/npgsql/npgsql) | PostgreSQL access (server) | PostgreSQL License |
| [Dapper](https://github.com/DapperLib/Dapper) | Database queries (server) | Apache-2.0 |
| [Blake3](https://github.com/xoofx/Blake3.NET) | Content hashes of file chunks | BSD-2-Clause (bundles the BLAKE3 reference implementation, CC0-1.0 / Apache-2.0) |
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) | Compressing file chunks | MIT |
| [PDFsharp](https://github.com/empira/PDFsharp) | Stamping published PDFs | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) | QR codes on published PDFs and two-factor set-up | MIT |
| Microsoft.AspNetCore.Authentication.JwtBearer, .Negotiate; Microsoft.Extensions.Hosting.WindowsServices, Configuration.*; System.Security.Cryptography.ProtectedData; and the .NET runtime | Server hosting, sign-in, Windows services, settings, saved sessions | MIT |

## Used only to build and test (not shipped)

| Package | Licence |
|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing, coverlet.collector | MIT |
| Testcontainers.PostgreSql | MIT |

## Autodesk software

Openhold works with Autodesk Inventor and AutoCAD **installed on the same PC**, and ships no Autodesk files:
- **Inventor:** Openhold loads Inventor's interop assembly (`Autodesk.Inventor.Interop.dll`) from the Inventor installation at run time.
- **AutoCAD:** the AutoCAD plugin is built against AutoCAD's own .NET assemblies, which AutoCAD provides when it loads the plugin.

Building Openhold from source needs those products installed. Autodesk, AutoCAD and Inventor are trademarks of Autodesk, Inc.; Openhold is not affiliated with or endorsed by Autodesk.
