# Third-party notices

The project's own source is distributed under [MIT](LICENSE). External dependencies and adapted components retain their original terms.

## Grok Bot runtime

The embedded runtime preserves its upstream [third-party notices](src/LLMWorkGUI.Infrastructure/GrokBot/Runtime/THIRD_PARTY_NOTICES.md) and [source manifest](src/LLMWorkGUI.Infrastructure/GrokBot/Runtime/source-manifest.json). Those notices include the MIT attribution to Tao Wen for the referenced grokbot2api work. The runtime notices are copied beside the runtime scripts in application builds.

## .NET and NuGet dependencies

Project files record the NuGet dependencies. Microsoft .NET/Extensions components, Microsoft.Data.Sqlite, SQLitePCLRaw, and their dependencies retain their own licenses and notices. The CI package preserves available NuGet license/notice files and the license and third-party notices from its .NET runtime packs.

External CLI clients are installed separately and are not redistributed in the application archive. Their names identify integrations and do not imply endorsement.
