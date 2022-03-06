﻿using Discord;
using Discord.Interactions;
using Fergun.Extensions;
using GTranslate;
using GTranslate.Translators;

namespace Fergun.Modules.Handlers;

public class TtsAutocompleteHandler : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var text = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();

        IEnumerable<ILanguage> languages = GoogleTranslator2
            .TextToSpeechLanguages
            .Where(x => x.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                        x.ISO6391.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                        x.ISO6393.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name);

        if (context.Interaction.TryGetLanguage(out var language))
        {
            languages = languages.Where(x => !x.Equals(language)).Prepend(language);
        }

        var results = languages
            .Select(x => new AutocompleteResult($"{x.Name} ({x.ISO6391})", x.ISO6391))
            .Take(25);

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}