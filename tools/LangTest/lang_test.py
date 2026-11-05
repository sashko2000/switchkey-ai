"""Check: how well the model knows the languages (forward and back translation).

Run: lang_test.bat. The result goes to lang_test_report.txt next to the script.
Better unload the model in KeyboardSwitcher first (tray icon menu -> "Unload model from memory").
"""
import os, sys, time
import openvino_genai as ov_genai

LANGS = {
    "en": "English", "uk": "Ukrainian", "sk": "Slovak", "es": "Spanish",
    "hi": "Hindi (in Devanagari script)", "ru": "Russian",
}
SENTENCES = [
    "Привет! Как у тебя дела?",
    "Завтра в 10 утра у нас встреча с клиентом, пожалуйста, не опаздывай.",
    "Спасибо за помощь, без тебя я бы не справился с этой задачей.",
    "Файл слишком большой, поэтому программа не может его открыть.",
]

def find_model():
    local = os.path.join(os.environ.get("LOCALAPPDATA", ""), "KeyboardSwitcher", "models", "Qwen3-8B-int4-cw-ov")
    here = os.path.dirname(os.path.abspath(__file__))
    npu = os.path.normpath(os.path.join(here, "..", "..", "..", "npu", "models", "Qwen3-8B-int4-cw-ov"))
    for d in (sys.argv[1] if len(sys.argv) > 1 else "", local, npu):
        if d and os.path.exists(os.path.join(d, "openvino_model.xml")):
            return d
    sys.exit("Модель Qwen3-8B-int4-cw-ov не найдена. Укажите папку модели: lang_test.bat <папка>")

def main():
    out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lang_test_report.txt")
    out = open(out_path, "w", encoding="utf-8")
    def log(s=""):
        print(s, flush=True)
        out.write(s + "\n"); out.flush()

    model = find_model()
    cache = os.path.join(os.environ.get("LOCALAPPDATA", ""), "KeyboardSwitcher", "ovcache")
    pipe, device = None, ""
    for dev in ("NPU", "GPU.0", "CPU"):
        try:
            cfg = {"CACHE_DIR": cache}
            if dev == "NPU":
                cfg.update({"MAX_PROMPT_LEN": 1024, "MIN_RESPONSE_LEN": 256})
            t0 = time.time()
            pipe = ov_genai.LLMPipeline(model, dev, **cfg)
            device = dev
            log(f"Модель: {model}\nУстройство: {dev}, загрузка {time.time() - t0:.0f} с\n")
            break
        except Exception as e:
            log(f"{dev}: не загрузилась ({e})")
    if pipe is None:
        sys.exit(1)

    gen = ov_genai.GenerationConfig()
    gen.max_new_tokens = 200
    gen.do_sample = False

    def translate(text, lang):
        prompt = (f"You are a professional translator. Translate the text below into {lang}. "
                  "Translate the meaning, never transliterate. Reply with the translation only.\n\n"
                  f"Text:\n{text}\n/no_think")
        t0 = time.time()
        r = pipe.generate(prompt, gen)
        r = str(r)
        if "</think>" in r:
            r = r.split("</think>", 1)[1]
        return r.strip(), time.time() - t0

    for code, name in LANGS.items():
        if code == "ru":
            continue
        log("=" * 70)
        log(f"{name}  ({code})")
        log("=" * 70)
        for s in SENTENCES:
            there, t1 = translate(s, name)
            back, t2 = translate(there, "Russian")
            log(f"RU:      {s}")
            log(f"-> {code}:   {there}   [{t1:.1f} с]")
            log(f"-> назад: {back}   [{t2:.1f} с]")
            log()
    log(f"Готово. Отчёт: {out_path}")

if __name__ == "__main__":
    main()
