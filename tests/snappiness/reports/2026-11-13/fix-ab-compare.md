# Snappiness: ab-before vs ab-fix

## CPU 4x

| Interaction | Metric | ab-before median | ab-before p95 | ab-fix median | ab-fix p95 | Change (median) | n |
|---|---|---|---|---|---|---|---|
| coldStart | session list shown | – | – | – | – | – | 0/0 |
|  | conversation shown | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
|  | JS decoded KB | – | – | – | – | – | 0/0 |
| openSession | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| switchLong | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| typing | keystroke→paint p95 | 384 | 489 | 78.4 | 209 | -80% | 8/8 |
|  | keystroke→paint median | 134 | 156 | 49.5 | 55.9 | -63% | 8/8 |
|  | INP (Event Timing) | 116 | 120 | 72.0 | 95.6 | -38% | 8/8 |
| sendPrompt | bubble shown | 325 | 344 | 310 | 347 | -5% | 8/8 |
|  | settled | 1632 | 1770 | 1156 | 1698 | -29% | 8/8 |
|  | long tasks | 1629 | 1761 | 1130 | 1704 | -31% | 8/8 |
|  | requests | 4.0 | 5.3 | 3.5 | 5.6 | -13% | 8/8 |
| railSessions | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| railSettings | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| railAnalytics | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| railFleet | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| openChanges | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| scrollLong | frame p95 | – | – | – | – | – | 0/0 |
|  | dropped frames / 3 s | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | scrolled px | – | – | – | – | – | 0/0 |
| newSessionPage | shown | – | – | – | – | – | 0/0 |
|  | settled | – | – | – | – | – | 0/0 |
|  | long tasks | – | – | – | – | – | 0/0 |
|  | requests | – | – | – | – | – | 0/0 |
| streamingList | long tasks / 6 s | 1131 | 1738 | 165 | 529 | -85% | 8/8 |
|  | worst frame | 83.4 | 237 | 108 | 188 | +30% | 8/8 |
|  | keystroke→paint p95 | 174 | 407 | 74.4 | 142 | -57% | 8/8 |
| streamingWatch | long tasks / 6 s | – | – | – | – | – | 0/0 |
|  | worst frame | – | – | – | – | – | 0/0 |
|  | keystroke→paint p95 | – | – | – | – | – | 0/0 |
