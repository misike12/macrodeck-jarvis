* Let Jarvis drink a tea. (I need to ask him for more detail later.) milk, no sugar 60 Celsius 4pm (Make an animation for the widget for him)
* Custom WakeWord
* Fully customizable widget
* Test all tools
* Add more actions, events, widget
* Reduce the test time
* Reduce latency
* Let jarvis move the cursor (drag and drop, scroll, right and left click, and all the mouse buttons)
* Test Vision
* Test Llama.cpp
* Clean up the folders
* Create a GitHub actions workflow
* Clean up all the comments or at least shrink them
* test 96kHz mic (or all the types of mic)
* Make widget Fully working and test it visually
* Buttons on widget
* fix the config flow completely
* add bump to beta15
* Test all scenarios
* Test all actions command events.
* Test widget in every possible way. (visually too)
* Test every freature.
* Check code. (fix code quality)
* Check against a real macrodeck
* Fix every bug.

## Done

Each line records what was actually changed and where it is, so the next person does not have to find
out again. The commit for each is on `main`.

| Item | What was done |
| --- | --- |
| Build warning-free | The dead streaming counter and unused constant left by the remainder rewrite are gone, and the ONNX result sets are indexed rather than went through LINQ. CI builds with `--warnaserror`. |
| Add bump to beta15 | `Directory.Packages.props` now pins `3.0.0-beta.15`, which is published. Build, tests and conformance are unchanged against it. |
| Create a GitHub actions workflow | Both workflows existed but did not enforce what they claimed. The build is warning-gated, `regenerate-conformance.ps1` now checks both suites' exit codes, and the committed reports are compared against the ones just produced so they cannot go stale. The project conformance suite was running twice and now runs once. |
| Clean up the folders | Four scratch files removed from `ideas/`, `.freebuff/` and `todo.md` added to `.gitignore` because they are the agent harness's own state. |
| fix the config flow completely | Eight settings the gate read and no step offered are now offered; four more had no field at all. `lifetime` was a number parsed as an enum and is gone. Every numeric bound is declared once. |
| Custom WakeWord | A word with no pinned model no longer loads the default one and listens for "hey jarvis". The integration asks whether a model can hear the configured word and falls back to the transcript engine, which matches any word. |
| test 96kHz mic (or all the types of mic) | Capture negotiates instead of assuming: the endpoint's own format first, then a ladder of rates, then its own channel count, then its own format. What was agreed is read back off the recorder and told to everything downstream. Multichannel packets are averaged to mono. Decimation now filters before thinning. |
| Let jarvis move the cursor | All five buttons plus triple click, resolved through one function the click and drag tools share. A cancelled drag releases the button and says so. A click travels as one batch. |
| Buttons on widget | The orb carries Talk and Stop, through the same paths a deck button and the wake word use. Stop is drawn but inert unless a turn is under way. |
| Test Vision | Verified against the real API. The default model was a 90b model that did not answer a screenshot inside the timeout; the default is now the 11b one, which answers in seconds and is correct. A live test pins it. |
| Fix every bug | Ten real defects, each with a test. The worst: every enum setting silently reverted to its default on the first reload, so a user who chose a self-hosted provider was posting their token to NVIDIA. |
| Reduce the test time | The settings fixtures no longer pay the host's 150 ms rate-limit gap. 880 tests. |
| Check code (fix code quality) | An audit pass over the whole plugin. Ten CERTAIN findings fixed, the rest recorded as known limitations. |

## Not done

Recorded rather than left to look finished.

* **Test Llama.cpp** and **Check against a real macrodeck**. No llama.cpp server was running here and no
  Macro Deck host is installed on this machine, so neither could be exercised. Both paths are unit tested
  against fakes, which is not the same thing.
* **Test the widget visually**. The orb's tree is materialized in tests across every configuration, which
  catches the materializer rules, and the live vision test proved the render pipeline reaches the host. What
  is missing is a person looking at it.
* **Add more actions, events, widget**, **Fully customizable widget**, **Test all scenarios**, **Test all
  actions command events**, **Test every freature**. The suite went from 592 to 880 tests covering the
  tools, the settings, the audio path at every rate and the orb, but "test everything" is not something a
  test suite can claim to have finished.
* **Reduce latency**. The largest remaining cost is measured, not guessed: reading the configuration is one
  host call per field and the host's rate limit spaces them, which is six seconds of waiting on every
  reconnect and every configuration change. That is a change to the host contract rather than to this code.
* **Clean up all the comments**. Comments were shortened as each area was touched. Several files still carry
  long explanations of bugs that are now fixed, which is the case this item exists for.
* **Let Jarvis drink a tea**. Still needs the detail.