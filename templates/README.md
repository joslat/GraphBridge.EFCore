# GraphBridge dotnet-new prototype

This source-only NuGet template produces a package-only console application and two offline query contract tests. It includes the exact live consumer from `eng/consumers/ProviderLiveConsumer.cs` during packing, so the guarded demo is maintained once. The generated project pins provider/EF/driver versions, carries a lock-file policy, and never references the GraphBridge checkout.

The template package has no DLL or dependency payload. Install the locally built `.nupkg`, then run:

```powershell
dotnet new install /path/to/GraphBridge.Templates.VERSION.nupkg
dotnet new graphbridge --name MyGraph --provider-version EXACT_LOCAL_PROVIDER_VERSION
```

Follow the generated README to restore from your local/private feed. There are no automatic post-actions. Public template-feed publication is deferred. Acceptance uses an isolated template hive and fresh project directory; it does not change the developer's installed templates. Template structure follows the [.NET template authoring documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/templates).
