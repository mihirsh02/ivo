namespace Ivo;

// The how-to guides in Settings → Assistant: training your own model, a dataset, a behaviour pack. Each is a few steps
// with an example file in the right format (GuideWindow).
public partial class MainWindow
{
    // a guide opens as one of Ivo's panels (Small); Esc or Done closes it, back to Settings if it came from there
    void Guide(string key, string title, GuideView.Step[] steps) => OpenFromSettings(key, title, "", "#A78BFA", new GuideView(steps, ClosePanel), scroll: false);

    void ModelGuide() => Guide("guide-model", "Train your own Ivo model", [
        new("What you'll make",
            "A small language model trained to talk like your Ivo, as one .gguf file (about 400 MB for 0.5B, 1 GB for 1.5B). Ivo runs it on this PC for Smarter chat.\n\n" +
            "You train it for free on a cloud GPU (Kaggle or Google Colab) in 30 to 60 minutes. You need: your training data (step 2), the behaviour pack your Ivo uses (step 4), and the script (step 4)."),
        new("Write your training data",
            "One example per line in a .jsonl file. Each shows a user message, a high-quality response, and optionally the relevant conversation context or instruction.\n\n" +
            "• name and personality: optional Ivo context used by the app\n" +
            "• turns: previous conversation, as [\"user\" or \"assistant\", text]\n" +
            "• notes: null, or a relevant instruction from your behaviour pack\n" +
            "• user: the message · reply: the response you want the model to learn",
            """
            {"name": "Sam", "personality": "", "turns": [], "notes": null, "user": "hii", "reply": "Hey! How's your day going?"}
            {"name": "Asha", "personality": "", "turns": [["user", "i had the worst day"], ["assistant", "I'm sorry, that sounds rough. Want to tell me what happened?"]], "notes": "They sound upset. Be empathetic and ask one gentle follow-up.", "user": "my project got rejected", "reply": "That must feel disappointing after putting in the work. Did they share what to improve?"}
            {"name": "", "personality": "Curious", "turns": [], "notes": "Answer the question directly, then add a useful detail.", "user": "why is the sky blue", "reply": "Air scatters blue light from the sun more than most other colours, so the sky looks blue to us."}
            {"name": "Leo", "personality": "", "turns": [], "notes": "When they ask about Ivo, explain the feature accurately and simply.", "user": "what is code mode", "reply": "Code mode is Ivo's built-in workspace for editing files, using terminals, and working with Git."}
            """, "training-data.jsonl"),
        new("Make it good",
            "• Variety beats volume: many different messages, each once. Repeating the same line teaches her to parrot it.\n" +
            "• Short, natural replies: one or two sentences for small talk, a little longer for real topics.\n" +
            "• Write users like real people: lowercase, slang, typos, emoji.\n" +
            "• Cover varied tasks and situations; include enough examples for each behavior you want the model to learn.\n" +
            "• Include relevant notes your behaviour pack sends (mood, short reactions, or questions), so it learns to follow them.\n" +
            "• 1,000–3,000 examples is plenty to start."),
        new("The training script",
            "This builds every example into exactly the prompt Ivo sends, from the behaviour pack you upload (persona, modes, sample lines), so the model learns on what it will really see. Then it trains a small add-on (LoRA) and saves one .gguf file.\n\n" +
            "Use the same behaviour pack you'll use in Ivo: the default (Assets\\ivo\\behaviour.json) or your own. Change BASE to train a bigger model.",
            """
            # Train Ivo's chat model. Run in a Kaggle or Colab notebook with a GPU.
            # Upload two files first: your training data (*.jsonl) and your behaviour pack (behaviour*.json).
            # (Colab: put them in /content; Kaggle: Add Input, they appear under /kaggle/input.)
            import subprocess, sys
            subprocess.run([sys.executable, "-m", "pip", "install", "-q", "unsloth"], check=True)

            import json, glob, re, torch
            from unsloth import FastLanguageModel
            from datasets import Dataset
            from transformers import TrainingArguments, Trainer, DataCollatorForSeq2Seq

            BASE = "Qwen/Qwen2.5-1.5B-Instruct"          # or Qwen/Qwen2.5-0.5B-Instruct (smaller, faster, less clever)
            HERE = ["/kaggle/input/**", "/content"]
            def find(pattern): return [f for h in HERE for f in glob.glob(f"{h}/{pattern}", recursive=True)]

            pack = json.loads(re.sub(r"^\s*//.*$", "", open(find("behaviour*.json")[0], encoding="utf-8").read(), flags=re.M))
            rows = [json.loads(l) for f in find("*.jsonl") for l in open(f, encoding="utf-8") if l.strip()]
            print(len(rows), "examples")

            # the prompt, exactly as Ivo builds it (Core/IvoAssistant.cs → Prompt)
            def system(r):
                age, name = r.get("age"), r.get("name", "")
                p = r.get("personality") or ""
                s = pack["persona"].replace("{a_age}", f"a {age}-year-old" if age else "a young") \
                                   .replace("{age_word}", f"{age}-year-old" if age else "young") \
                                   .replace("{owner}", f"{name}'s" if name else "the user's") \
                                   .replace("{personality}", f"Your personality: {p}." if p else pack.get("personality", ""))
                s += " " + pack.get("clean", "") + " "
                if name: s += f"Their name is {name}. "
                return s.strip()

            def prompt(r):
                sys_text = system(r)
                out = f"<|im_start|>system\n{sys_text}<|im_end|>\n"
                for u, h in pack.get("examples", []):
                    out += f"<|im_start|>user\n{u}<|im_end|>\n<|im_start|>assistant\n{h}<|im_end|>\n"
                turns = r.get("turns") or []
                for who, text in turns[max(0, (len(turns) - 3) // 4 * 4):]:
                    out += ("<|im_start|>user\n" if who in (True, "user") else "<|im_start|>assistant\n") + text + "<|im_end|>\n"
                if r.get("notes"): out += "<|im_start|>system\nFor this reply: " + r["notes"] + "<|im_end|>\n"
                return out + "<|im_start|>user\n" + r["user"] + "<|im_end|>\n<|im_start|>assistant\n"

            model, tok = FastLanguageModel.from_pretrained(BASE, max_seq_length=1024, load_in_4bit=False)
            model = FastLanguageModel.get_peft_model(model, r=16, lora_alpha=16, lora_dropout=0, use_gradient_checkpointing="unsloth",
                target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"])

            def encode(r):
                p = tok(prompt(r), add_special_tokens=False)["input_ids"]
                a = tok(r["reply"] + "<|im_end|>", add_special_tokens=False)["input_ids"]
                ids = (p + a)[-1024:]
                return {"input_ids": ids, "attention_mask": [1] * len(ids), "labels": ([-100] * len(p) + a)[-1024:]}   # it learns the reply only

            Trainer(model=model, train_dataset=Dataset.from_list([encode(r) for r in rows]).shuffle(seed=1),
                    data_collator=DataCollatorForSeq2Seq(tok, padding=True, label_pad_token_id=-100),
                    args=TrainingArguments(output_dir="out", per_device_train_batch_size=4, gradient_accumulation_steps=4, num_train_epochs=3,
                                           learning_rate=2e-4, lr_scheduler_type="cosine", warmup_steps=10, logging_steps=10, save_strategy="no", report_to="none",
                                           fp16=not torch.cuda.is_bf16_supported(), bf16=torch.cuda.is_bf16_supported())).train()

            model.save_pretrained_gguf("ivo", tok, quantization_method="q4_k_m")
            gguf = [f for f in glob.glob("ivo*/*.gguf") + glob.glob("ivo*.gguf") if "q4_k_m" in f.lower()][0]
            print("Done:", gguf, "→ download it and pick it in Ivo")
            """, "train_ivo.py"),
        new("Run it on Kaggle (or Colab)",
            "Kaggle (free, phone-verified account):\n" +
            "1. kaggle.com → Create → New Notebook.\n" +
            "2. Right panel → Session options: Accelerator GPU T4 x2, Internet On.\n" +
            "3. Add Input → Upload → your .jsonl and your behaviour pack .json.\n" +
            "4. Paste the script into a cell and press Run All. Keep the tab open.\n" +
            "5. When it prints Done, find the .gguf under Output → /kaggle/working, ⋮ → Download.\n\n" +
            "Colab: Runtime → Change runtime type → T4 GPU; upload the two files with the 📁 panel; run the script; right-click the .gguf → Download."),
        new("Use it in Ivo",
            "Settings → Assistant → Smarter chat → Add your own model…, and pick the .gguf. It is copied into Ivo’s models folder (so you can delete the download) and replaces the standard model. Add more and pick which one she uses.\n\n" +
            "Keep the same behaviour pack on in Settings → Assistant → Behaviour packs that you trained with: the model has learnt that persona, those modes and those notes, so the app sending the same keeps her at her best."),
    ]);

    void DatasetGuide() => Guide("guide-dataset", "Make a dataset", [
        new("What a dataset is",
            "Fixed replies Ivo gives when a message matches: things people might say, and how she answers. Datasets are checked before what she knows built in, so they can add to it or override it.\n\n" +
            "Use focused examples to add reliable facts, useful phrasing, or domain-specific answers. Ivo's existing app rules still apply."),
        new("The format",
            "A JSON file with a name and a list of entries. Each entry:\n" +
            "• match: example phrases. Ivo understands them with typos, slang and other wording.\n" +
            "• words (instead of or as well as match): keywords with weights, \"gym:3 workout:2\"; it matches when they add up to 3.\n" +
            "• reply: one or more replies; one is picked at random. {name} is the user's name.\n" +
            "• more: what she says if they then ask \"how?\".\n" +
            "Use the same field names shown here; each entry needs a reply and at least one match phrase or weighted keyword.",
            """
            {
              "name": "Everyday science and fitness",
              "entries": [
                {
                  "match": ["why is the sky blue", "what makes the sky look blue"],
                  "reply": ["Air scatters blue light from the sun more than most other colours, which makes the sky look blue."]
                },
                {
                  "match": ["how do I start exercising", "beginner workout advice"],
                  "reply": ["Start with an activity you can repeat comfortably, build up gradually, and rest when you need it."],
                  "more": "A short walk or simple bodyweight routine can be a good first step."
                },
                {
                  "words": "photosynthesis:3 plants:1",
                  "reply": ["Photosynthesis is how plants use light to turn water and carbon dioxide into stored energy."]
                }
              ]
            }
            """, "my-dataset.json"),
        new("Make it good",
            "• A few different example phrases per entry: she matches the meaning, so you don't need every spelling.\n" +
            "• Several replies per entry keep her from sounding repetitive.\n" +
            "• Keep replies short and in her voice.\n" +
            "• Entries are checked in order: put the more specific ones first."),
        new("Add it",
            "Settings → Assistant → Packs → Datasets → Add a dataset…, and pick your .json. It's copied in and sealed with your app lock; switch it on or off there, or remove it."),
    ]);

    void BehaviourGuide() => Guide("guide-behaviour", "Make a behaviour pack", [
        new("What a behaviour pack is",
            "A JSON file that guides Ivo's model: its role, response style, examples, context-aware notes, and phrases to avoid.\n\n" +
            "Ivo's own pack (kind and caring) is always underneath. Yours go on top, in the order you add them: your settings replace matching parts, and the rest stays Ivo's."),
        new("The format",
            "Every part is optional: put in only what you want to change.\n" +
            "• persona: Ivo's role. Supported placeholders such as {owner} and {personality} are filled in by the app.\n" +
            "• personality: used when none is picked in Settings.\n" +
            "• clean: optional general-audience guidance.\n" +
            "• examples: sample exchanges as [message, reply]. Yours replace the defaults; add \"addExamples\": true to append instead.\n" +
            "• notes: added for a message when it fits (next step).\n" +
            "• avoid: phrases a reply shouldn't have, and how much each costs; a costly reply is tried again.\n" +
            "• maxEmoji: the most emoji in one reply.\n" +
            "• replies: optional fixed lines without the model. Use \"match\" for message patterns or \"id\" for supported Ivo intents; Ivo's app-level rules still apply.",
            """
            {
              "name": "Calm study buddy",
              "personality": "You are calm, patient and encouraging, like a good study partner.",
              "clean": "Be supportive, clear, and appropriate for a general audience.",
              "examples": [
                ["i cant focus", "Let's make it small: 25 minutes on one thing. What's the one thing?"],
                ["i finished my assignment", "That's a big one off your plate. How do you feel?"]
              ],
              "addExamples": true,
              "notes": [
                { "id": "stressed", "when": ["mood:Stressed"], "say": "They are stressed. Be calm, and suggest one small next step." },
                { "when": ["question"], "match": "\\b(exam|test|assignment|deadline)\\b", "say": "It's about their studies: be practical and specific." },
                { "when": ["short", "!question"], "say": "Keep your reply to one short sentence." }
              ],
              "avoid": [
                { "match": "\\bgood luck\\b", "cost": 2 }
              ],
              "maxEmoji": 1
            }
            """, "my-behaviour.json"),
        new("Notes and signals",
            "A note is said to the model for one reply when its \"when\" signals all fit (and its \"match\", a pattern on the message, if it has one). Put ! in front of a signal for \"not\".\n\n" +
            "• mood:Happy, mood:Excited, mood:Sad, mood:Stressed, mood:Angry, mood:Rude, mood:Flirty, mood:Bored, mood:Confused\n" +
            "• question: it asks something\n• short: three words or fewer, not a question\n• rude: swearing or crude words\n" +
            "• meaning: slang or short forms were interpreted ({meaning} says what)\n• morning, afternoon, evening, night: time of day\n\n" +
            "In \"say\", {her} is her last line and {meaning} is what their slang meant.\n\n" +
            "A note with the same \"id\" as one of Ivo's replaces it. Use concise, specific instructions that help the model respond appropriately to the situation.",
            """
            { "id": "stressed", "when": ["mood:Stressed"], "say": "They are stressed. Be calm and suggest one manageable next step." }
            { "when": ["question"], "say": "Answer the question directly before adding related context." }
            { "when": ["short"], "match": "^(ok|k|hmm|lol)$", "say": "It's a small reaction to \"{her}\". Answer lightly, then ask something." }
            """),
        new("Add it",
            "Settings → Assistant → Behaviour packs → Add a behaviour pack…, and pick your .json. It goes on top of Ivo's own and is sealed with your app lock; switch it off or remove it there.\n\n" +
            "Training your own model? Train it with the same pack (the training guide's script reads it), so what she learnt matches what the app sends."),
    ]);
}
