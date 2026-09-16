using System.Text.Json.Serialization;

namespace BootVideoManager.Core.Api.Dto;

/// <summary>
/// Source-generated serializer metadata (no reflection: faster start-up, trimming/AOT friendly).
/// Numbers are also accepted as strings to survive loose back-end typing.
/// </summary>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PostDto))]
internal sealed partial class RepoJsonContext : JsonSerializerContext;
