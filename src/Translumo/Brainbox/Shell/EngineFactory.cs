using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brainbox.Core.Diagnostics;
using Brainbox.Core.Settings;
using Brainbox.Core.Translation;
using Translumo.Translation.Google;

namespace Brainbox.Desktop.Shell
{
    /// <summary>
    /// Builds the provider chain for the chosen engine (§8): LM Studio / Ollama / any
    /// OpenAI-compatible server, the other local server as an automatic local fallback, the
    /// Translumo cloud LLM profiles, and optionally Google Translate as a last resort.
    /// </summary>
    public static class EngineFactory
    {
        public static string DisplayName(TranslationEngineKind kind) => kind switch
        {
            TranslationEngineKind.LmStudio => "Local AI (LM Studio)",
            TranslationEngineKind.Ollama => "Local AI (Ollama)",
            TranslationEngineKind.CustomOpenAi => "Custom OpenAI-compatible",
            TranslationEngineKind.Google => "Google Translate (online)",
            TranslationEngineKind.Translumo => "Translumo cloud AI profile",
            _ => kind.ToString(),
        };

        public static (ResilientTranslator Chain, IVisionTranslator Vision, List<IDisposable> Owned) Build(
            BrainboxSettings s, Func<long> clock, ILog log, Func<ITranslationProvider> translumoProvider = null)
        {
            var providers = new List<ITranslationProvider>();
            var owned = new List<IDisposable>();
            IVisionTranslator vision = null;
            var timeout = TimeSpan.FromSeconds(s.RequestTimeoutSeconds);

            OpenAiCompatibleProvider Local(string name, string url, string model, string key = null)
            {
                var p = new OpenAiCompatibleProvider(name, url, model, key, isLocal: IsLoopback(url), timeout: timeout);
                owned.Add(p);
                return p;
            }

            switch (s.Engine)
            {
                case TranslationEngineKind.LmStudio:
                {
                    var lm = Local("LM Studio", s.LmStudioEndpoint, s.Model);
                    providers.Add(lm);
                    vision = lm;
                    // Ollama as a silent local fallback (model auto-picked; fails fast if not running).
                    providers.Add(Local("Ollama", s.OllamaEndpoint, null));
                    break;
                }

                case TranslationEngineKind.Ollama:
                {
                    var ol = Local("Ollama", s.OllamaEndpoint, s.Model);
                    providers.Add(ol);
                    vision = ol;
                    providers.Add(Local("LM Studio", s.LmStudioEndpoint, null));
                    break;
                }

                case TranslationEngineKind.CustomOpenAi when !string.IsNullOrWhiteSpace(s.CustomEndpoint):
                {
                    var c = Local("Custom AI", s.CustomEndpoint, s.Model, s.CustomApiKey);
                    providers.Add(c);
                    vision = c;
                    break;
                }

                case TranslationEngineKind.Translumo when translumoProvider != null:
                    providers.Add(translumoProvider());
                    break;

                case TranslationEngineKind.Google:
                    providers.Add(Google());
                    break;
            }

            if (s.CloudFallback && s.Engine != TranslationEngineKind.Google) providers.Add(Google());
            if (providers.Count == 0) log.Warn("No translation engine configured.");

            return (new ResilientTranslator(providers, clock, log), vision, owned);
        }

        public static ITranslationProvider Google()
        {
            var google = new AutoSourceGoogleTranslator();
            return new DelegateTranslationProvider("Google Translate", isLocal: false,
                (text, target, source, ct) => google.TranslateAsync(text, GoogleCode(target)), maxParallel: 3);
        }

        private static string GoogleCode(string iso) => iso switch
        {
            "zh" => "zh-CN",
            "zh-TW" => "zh-TW",
            "he" => "iw",
            _ => iso,
        };

        public static bool IsLoopback(string url)
        {
            try
            {
                return new Uri(OpenAiCompatibleProvider.NormalizeBaseUrl(url)).IsLoopback;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Lists the models offered by the endpoint of the given engine (Settings "Refresh models").</summary>
        public static async Task<IReadOnlyList<string>> ListModelsAsync(BrainboxSettings s, CancellationToken ct)
        {
            var url = s.Engine switch
            {
                TranslationEngineKind.Ollama => s.OllamaEndpoint,
                TranslationEngineKind.CustomOpenAi => s.CustomEndpoint,
                _ => s.LmStudioEndpoint,
            };
            using var p = new OpenAiCompatibleProvider("probe", url, null, s.Engine == TranslationEngineKind.CustomOpenAi ? s.CustomApiKey : null, timeout: TimeSpan.FromSeconds(5));
            return await p.ListModelsAsync(ct).ConfigureAwait(false);
        }
    }
}
