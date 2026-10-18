// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntimeGenAI;

// Level D captions (phase 5b, decision U4): Qwen3.5-9B or Qwen2.5-VL-7B (ONNX Runtime GenAI, CUDA) in a process of its own.
// Protocol, one JSON object per line (UTF-8):
//   start:    PhotoFlow.Vlm --model <folder>   → {"type":"ready"} or {"type":"error","message":...}
//   request:  {"id":1,"image":"<jpeg>","prompt":"...","maxTokens":128}
//   answer:   {"id":1,"text":"...","tokens":n,"ms":t} or {"id":1,"error":"..."}
//   "quit" or end of input closes the process.
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

// Held for the whole process: its disposal shuts the library down after every model is released (otherwise the
// process crashes on exit).
using var oga = new OgaHandle();
var modelPath = args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault();
if (modelPath is null || !Directory.Exists(modelPath))
{
    Write(new JsonObject { ["type"] = "error", ["message"] = "Usage: PhotoFlow.Vlm --model <folder>" });
    return 2;
}

Model model;
Tokenizer tokenizer;
MultiModalProcessor processor;
bool qwen35;
try
{
    qwen35 = IsQwen35(modelPath);
    model = new Model(modelPath);
    tokenizer = new Tokenizer(model);
    processor = new MultiModalProcessor(model);
}
catch (Exception e) when (e is OnnxRuntimeGenAIException or IOException or DllNotFoundException or BadImageFormatException or JsonException)
{
    Write(new JsonObject { ["type"] = "error", ["message"] = e.Message });
    return 3;
}

Write(new JsonObject { ["type"] = "ready" });
string? line;
while ((line = Console.ReadLine()) is not null && line != "quit")
{
    long id = 0;
    try
    {
        var request = JsonNode.Parse(line)!;
        id = request["id"]!.GetValue<long>();
        var image = request["image"]!.GetValue<string>();
        var prompt = request["prompt"]!.GetValue<string>();
        var maxTokens = request["maxTokens"]?.GetValue<int>() ?? 128;
        var clock = Stopwatch.StartNew();
        var (text, tokens) = Caption(model, tokenizer, processor, image, Chat(prompt, qwen35), maxTokens);
        Write(new JsonObject { ["id"] = id, ["text"] = text, ["tokens"] = tokens, ["ms"] = clock.ElapsedMilliseconds });
    }
    catch (Exception e) when (e is OnnxRuntimeGenAIException or IOException or JsonException or InvalidOperationException or NullReferenceException or FormatException)
    {
        Write(new JsonObject { ["id"] = id, ["error"] = e.Message });
    }
}

processor.Dispose();
tokenizer.Dispose();
model.Dispose();
return 0;

// The model type written by the Model Builder in genai_config.json ("qwen3_5…" or "qwen2_5_vl").
static bool IsQwen35(string modelPath)
{
    var config = JsonNode.Parse(File.ReadAllText(Path.Combine(modelPath, "genai_config.json")));
    return config?["model"]?["type"]?.GetValue<string>().StartsWith("qwen3_5", StringComparison.Ordinal) == true;
}

// The chat of one image and the prompt. Qwen3.5 thinks before answering unless the assistant turn opens with an empty
// think block (its chat template with enable_thinking=false): a caption needs no reasoning. Qwen2.5-VL has its system line.
static string Chat(string prompt, bool qwen35) => qwen35
    ? "<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>" + prompt + "<|im_end|>\n"
        + "<|im_start|>assistant\n<think>\n\n</think>\n\n"
    : "<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n"
        + "<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>" + prompt + "<|im_end|>\n"
        + "<|im_start|>assistant\n";

// Greedy generation on one image. The KV cache is sized for MaxLength tokens: an image of at most 1024 px is at most
// ~1,370 image tokens with Qwen2.5-VL (28 px each) and ~1,000 with Qwen3.5 (32 px), plus the prompt and the caption.
static (string Text, int Tokens) Caption(Model model, Tokenizer tokenizer, MultiModalProcessor processor, string imagePath, string chat, int maxTokens)
{
    using var images = Images.Load([imagePath]);
    using var inputs = processor.ProcessImages(chat, images);
    using var parameters = new GeneratorParams(model);
    parameters.SetSearchOption("do_sample", false);
    parameters.SetSearchOption("max_length", MaxLength);
    using var generator = new Generator(model, parameters);
    generator.SetInputs(inputs);
    using var stream = tokenizer.CreateStream();
    var text = new StringBuilder();
    var count = 0;
    while (count < maxTokens)
    {
        generator.GenerateNextToken();
        if (generator.IsDone())
        {
            break;
        }

        text.Append(stream.Decode(generator.GetNextTokens()[0]));
        count++;
    }

    return (text.ToString().Trim(), count);
}

static void Write(JsonObject message)
{
    Console.Out.WriteLine(message.ToJsonString());
    Console.Out.Flush();
}

internal static partial class Program
{
    private const int MaxLength = 2048;
}
