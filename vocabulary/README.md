# Vocabulary

`ramplus.tsv`: the 4,585 tags of RAM++ in the model's order, with an Italian label and a default category
(`Persone`, `Luoghi`, `Natura`, `Veicoli`, `Eventi`, `Animali`, or none).

- Keys: the RAM++ tag list (`ram_tag_list.txt` of [recognize-anything](https://github.com/xinyu1205/recognize-anything),
  Apache-2.0), unchanged; they are the stable internal ids of the tags (spec §8).
- Italian labels and categories: prepared with an LLM during development (decision F3-2, 13 Oct 2026). Every label
  starts as "not reviewed": PhotoFlow writes only the English key until the user approves the Italian label in the
  Vocabulary section.

Embedded in `PhotoFlow.Tagging` at build time.
