# Mapping assist: an AI field mapper that only suggests

[![Test and deploy](https://github.com/hamzamehmood46/mapping-assist-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/mapping-assist-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/mapping-assist-dotnet](https://hamzamehmood46.github.io/mapping-assist-dotnet/)**

Paste a source sample (XML or EDI-style segments) and describe the target schema. An assistant proposes which source field belongs in which target field, with a confidence and a one-sentence reason. **Nothing is mapped until a person approves it.** You can change the target or the conversion first, reject a bad suggestion, or withdraw an approval. Output is built from approved mappings only. Written in C#, with a Blazor WebAssembly demo that runs entirely in your browser.

> **The model is a stand-in.** `FakeChatClient` compares field names: it splits them into words, treats a short built-in list of word groups as the same word (qty and quantity, zip and postal, buyer and customer) and checks that a value looks like a date or a number. It does not understand meaning and it will make wrong suggestions. It is deterministic and needs no network or key. In the EDI example it proposes mapping the order-type code `BEG.02` to `/Order/Currency` at 50% confidence because both names contain "order". That is a real wrong answer left in on purpose: it shows why there is an Approve button.

## Try it

1. Open the demo and press **Suggest mappings**. Eleven suggestions appear for the partner XML, each with a confidence and a reason.
2. Approve a few. The output panel builds an internal XML document from approved mappings only. Dates are converted (`03/14/2026` becomes `2026-03-14`) and prices lose their currency sign (`$14.50` becomes `14.50`).
3. Before approving, change a suggestion's target or conversion in its drop-downs. A target can only be mapped once.
4. Press **EDI order &rarr; internal XML**, then **Suggest mappings**, and reject the `BEG.02` suggestion.
5. Press **Partner XML &rarr; EDI order** to go the other way and get segments like `N4*Baltimore**21201~`.

## How it works

```
source sample ─► read leaf fields ─┐
target schema ─► read fields ──────┤► redact values ─► model ─► checked proposals ─► Pending suggestions
                                                                                       │ Approve (optionally edited)
                                                                                       ▼
                                                              approved mappings ─► transforms ─► XML or EDI-style output
```

| Piece | Behavior |
|---|---|
| **Readers** | XML leaves (and attributes) become paths such as `/PurchaseOrder/ShipTo/City`. EDI segments become positional fields such as `N4.01`; a handful of elements the demo knows (`BEG.03`, `N4.03`, `PO1.02` and a few more) carry a readable label, the rest are positions only. A DOCTYPE is refused, so a sample cannot read a file. Bad input gives an error message, not an exception. Only the first of a repeated element or segment is read. |
| **Target schema** | One field per line: `path | type | description`. Type is string, number or date. |
| **Redaction** | Email addresses and phone numbers in sample values are replaced with `[email]` and `[phone]` before anything goes to a model. Names and street addresses are not detected. Values are cut to 60 characters. |
| **Model checks** | The model must return JSON. Every source and target must exist in the lists it was given, each target may be used once, the conversion must be one of six names, confidence must be from 0 to 1 and the reason is required. A bad answer gets one retry that says what was wrong, then no suggestions and no exception. Calls time out after 30 seconds. |
| **No code from the model** | The only conversions are `copy`, `trim`, `upper`, `lower`, `isodate` and `number`. The model picks a name; it cannot supply code. A value that cannot be converted is left out and reported. |
| **Data, not instructions** | Field lists go between `<source>` and `<target>` tags and the model is told never to follow instructions found in them. Angle brackets in names, values and descriptions are replaced, so text cannot close a tag. |
| **Suggest-only** | A suggestion is Pending, Approved or Rejected. Only Approve adds a mapping. A rejected pair is not suggested again. Asking again keeps approved and rejected items and replaces pending ones. |

## Using a real model

Set environment variables; nothing is read from code and the key is never printed.

| Variable | Meaning |
|---|---|
| `MAP_AI_PROVIDER` | `fake` (default), `ollama` or `openai-compatible` |
| `MAP_AI_ENDPOINT` | Server address. Ollama defaults to `http://localhost:11434`. |
| `MAP_AI_MODEL` | Model name, required for `ollama` and `openai-compatible` |
| `MAP_AI_API_KEY` | Bearer token for a hosted service, if it needs one |

`MapClients.Create(MapSettings.FromEnvironment())` returns an `IChatClient` to give to `MappingAssistant`. **The Ollama and OpenAI-compatible clients are tested against a stub HTTP server only. I have not run them against a live model.** The browser demo can only use the stand-in, because a static page cannot safely hold a key.

## Honest limits

- The demo's "AI" is name comparison. It shows the workflow and its safeguards, not how well a language model would map real partner data.
- Repeating groups are not handled. Only the first line item is read, so a mapping describes one line, not a list. The output has one `<Item>`.
- The EDI reader is not an X12 parser. It splits on `~` and `*`, knows names for about fifteen elements and has no loops, qualifiers or validation.
- Matching is by words. A different word for the same thing (anything outside the built-in groups) will not match, and a shared word can match the wrong field.
- The target schema is a simple list, not an XSD. There is no validation of the output against a schema.
- Everything is in memory; reloading resets the page. The sample data is made up.

## How it is built

```
src/Mapping.Core   Readers, transforms, output writer, MappingAssistant, MappingSession, FakeChatClient, HTTP clients, Redactor
src/Mapping.Web    Blazor WebAssembly demo
tests/             xUnit tests for the core
```

The only package behind the core is `Microsoft.Extensions.AI.Abstractions`, for `IChatClient`. JSON is built with `JsonObject` rather than anonymous types so it survives trimming in the published WebAssembly app; I checked a Release publish in the browser.

## Tests

```bash
dotnet test
```

118 tests, all offline. They cover both readers (XML, DOCTYPE refusal, repeated elements, EDI positions, error messages), every conversion and its failures, the output writer (nesting, escaping, attributes, EDI gaps, conflicts), what the stand-in suggests for the bundled samples, redaction and value limits, tag-escaping, every kind of bad model answer and its retry, timeouts, the approve, edit, reject and withdraw rules, end-to-end runs in both directions, and the settings and HTTP clients against a stub server.

I have not run deliberate-breakage checks on this project, so I cannot say how many mistakes the tests would miss.
