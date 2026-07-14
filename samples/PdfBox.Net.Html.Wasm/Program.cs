using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using PdfBox.Net.Html.Wasm;
using PdfBox.Net.Util;
using System.Text;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

WebAssemblyHost host = builder.Build();
IJSInProcessRuntime javascript = (IJSInProcessRuntime)host.Services.GetRequiredService<IJSRuntime>();
PdfStringNormalization.CompatibilityNormalizer = (value, normalizationForm) => javascript.Invoke<string>(
    "unpdf.normalizeCompatibility",
    value,
    normalizationForm == NormalizationForm.FormKC ? "NFKC" : "NFKD");

await host.RunAsync();
