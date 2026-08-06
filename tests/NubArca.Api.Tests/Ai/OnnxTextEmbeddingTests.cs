using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;
using HuggingFaceTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace NubArca.Api.Tests.Ai;

public sealed class OnnxTextEmbeddingTests
{
    private static AiProfile Profile() => new()
    {
        Id = Guid.NewGuid(),
        Key = OnnxImageModels.SiglipSo400mProfileKey,
        ConfigHash = OnnxImageModels.SiglipSo400mKey,
        Capability = AiCapabilities.ImageEmbedding,
        Modality = AiModalities.Image,
        Dimension = 1152,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    private static OnnxTextEmbedder Embedder(string? dir)
    {
        var options = Options.Create(new AiOptions { Onnx = new AiOnnxOptions { ModelDir = dir } });
        return new OnnxTextEmbedder(
            options, new OnnxInferenceSessionFactory(options, NullLogger<OnnxInferenceSessionFactory>.Instance));
    }

    [Fact]
    public void Catalog_Uses_One_1152_Dim_Multimodal_Profile()
    {
        var pair = Assert.Single(OnnxImageModels.ProfileToCatalogKey);
        Assert.Equal(OnnxImageModels.SiglipSo400mProfileKey, pair.Key);
        var config = OnnxImageModels.Catalog[pair.Value];
        Assert.Equal(1152, config.Dimension);
        Assert.Equal(64, config.TextSequenceLength);
        Assert.Equal("image_embeds", config.OutputTensor);
        Assert.Equal("text_embeds", config.TextOutputTensor);
    }

    [Fact]
    public void Readiness_Requires_Text_Tower_And_Exact_Tokenizer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"onnx-text-{Guid.NewGuid():N}");
        var modelDir = Path.Combine(root, OnnxImageModels.SiglipSo400mKey);
        Directory.CreateDirectory(modelDir);
        try
        {
            using var embedder = Embedder(root);
            var missingModel = embedder.CheckReadiness(Profile());
            Assert.Equal("onnx-text-model-not-found", missingModel.Reason);

            File.WriteAllText(Path.Combine(modelDir, OnnxImageModels.DefaultTextModelFile), "dummy");
            var missingTokenizer = embedder.CheckReadiness(Profile());
            Assert.Equal("onnx-tokenizer-not-found", missingTokenizer.Reason);

            File.WriteAllText(Path.Combine(modelDir, OnnxImageModels.DefaultTokenizerFile), "{}");
            Assert.True(embedder.CheckReadiness(Profile()).IsReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Native_Tokenizer_Loads_And_Applies_Fixed_Length_Policy()
    {
        const string json = """
        {
          "version":"1.0",
          "truncation":{"direction":"Right","max_length":64,"strategy":"LongestFirst","stride":0},
          "padding":{"strategy":{"Fixed":64},"direction":"Right","pad_to_multiple_of":null,"pad_id":0,"pad_type_id":0,"pad_token":"<pad>"},
          "added_tokens":[
            {"id":0,"content":"<pad>","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":1,"content":"<unk>","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true}
          ],
          "normalizer":{"type":"Lowercase"},
          "pre_tokenizer":{"type":"Whitespace"},
          "post_processor":null,
          "decoder":null,
          "model":{"type":"WordLevel","vocab":{"<pad>":0,"<unk>":1,"hello":2},"unk_token":"<unk>"}
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"tokenizer-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            using var tokenizer = HuggingFaceTokenizer.FromFile(path);
            var encoding = tokenizer.Encode("HELLO", addSpecialTokens: true).Single();
            Assert.Equal(64, encoding.Ids.Count);
            Assert.Equal((uint)2, encoding.Ids[0]); // lowercase normalizer applied
            Assert.All(encoding.Ids.Skip(1), id => Assert.Equal((uint)0, id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Siglip2_Fixed_Padding_Attends_Every_Position()
    {
        var mask = OnnxTextEmbedder.BuildFixedPaddingAttentionMask(64);

        Assert.Equal(64, mask.Length);
        Assert.All(mask, value => Assert.Equal(1L, value));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnnxTextEmbedder.BuildFixedPaddingAttentionMask(0));
    }
}
