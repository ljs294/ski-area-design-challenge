**Simulation design interview — choices for the player and for the implementation**

Archived companion to the [working plan](simulation-time-and-operations.md). The user's revised implementation request supersedes these questions. See the [implementation record](../dual-clock-implementation.md) for current decisions, behavior and verification; the questions below are retained as discussion history.

**Purpose and pacing.** The source conversation already favors every real calendar day with fast-forward through quiet periods. These questions refine that preference.

1. **Q01:** What should feel best: watching a mountain operate, fixing operational bottlenecks, building and expanding, or developing a successful business over many years? Rank them.
2. **Q02 — partly answered:** The user wants mountain activities on the micro clock to take about **ten minutes at 1x**, with a clean seconds/minutes ratio if useful. **Follow-up pending:** does this measure the whole open-to-close day or an individual guest's lift/run cycle? Initial operating hours are also being clarified.
3. **Q03:** How many hours of actual play should a typical complete winter take for an attentive player? What about an experienced player using automation aggressively?
4. **Q04:** In a 30-minute play session, what would feel satisfying to accomplish: one busy day, a week, a major storm cycle, a construction project, or something else?
5. **Q05:** Confirm real consecutive calendar days for new play. Should representative weeks remain an optional mode for new games, or only a compatibility path for older saves?
6. **Q06:** Should the player choose opening and closing dates based on conditions, or should a scenario impose a fixed season? Can unusually cold locations run longer seasons?
7. **Q07:** How much of the night should a player normally watch, and should preparation, operations, sweep, and overnight each have configurable pacing?
8. **Q08:** Should summer remain deliberate planning turns, run continuously with construction/jobs, or let the player choose? Are summer guests or summer operations a future goal?

**Speed controls and time behavior.** Calendar rate, simulation rules, and visual motion have different effects.

9. **Q09 — answered:** A small fixed set tuned with the user: **1x, 2x, 8x, and 64x**. Fewer player settings; designer tuning remains available in configuration.
10. **Q10:** Which controls should be available to ordinary players, advanced-settings users, and only you as the designer? Include day duration, skier/carrier/groomer motion, guest density, and population.
11. **Q11:** Should speed labels be familiar multipliers, descriptive names, world-minutes-per-real-minute, estimated minutes per operating day, or a combination?
12. **Q12:** Is true 1:1 world time an important supported mode for relaxing observation, or would an adjustable slow preset be sufficient?
13. **Q13:** When speed increases, should visible motion accelerate proportionally, increase gently to a cap, or remain at the chosen motion rate?
14. **Q14:** Should closing automatically accelerate into night and opening automatically slow down? Should a manual speed change override the profile until the next phase or indefinitely?
15. **Q15:** Which advance targets matter first: next opening, closing, local time/date, weekend, first snowfall, favorable snowmaking, completed job, or next actionable alert?
16. **Q16:** When the app is minimized or a browser tab is hidden, should world time pause, continue if possible, or later catch up? After the app is closed, should any offline progression occur?

**Guest identity and the limits of readable motion.** The user has selected a real guest with representative movement and clear approximations.

17. **Q17 — answered:** A clicked/followed skier represents **a real simulated guest, with representative motion between meaningful events; show any approximation clearly**.
18. **Q18 — answered:** Offer an explicit follow mode that slows the whole simulation; otherwise reconcile at event boundaries. Follow-up: what slowdown feels right, and should leaving follow mode restore the previous speed automatically?
19. **Q19:** Which discontinuities are acceptable when a guest changes state: instant snap, brief fade, accelerated final motion, or stopping the view with an explanation? Does the answer change for a selected guest?
20. **Q20:** Which parts of an individual's day must be exact and inspectable: actual waits, chosen runs, money, party members, satisfaction, needs, injuries, lodging, and route history?
21. **Q21:** Should a pinned guest remain visible at every speed and zoom? If not, should their indicator remain available in the existing inspector?
22. **Q22:** Should guests be persistent named people across visits and seasons, recognizable repeat visitors with compressed history, or individual identities only within each visit?
23. **Q23:** How prominent should parties/families be visually: visibly skiing and queuing together, only shared decisions, or optionally visible grouping?
24. **Q24:** Does watching someone change your decisions—for example, spotting a difficult junction or bad queue—or is following mainly for personality and atmosphere? Give a concrete example.
25. **Q25:** Is it acceptable that at high speed a guest's details report their current committed state while some intermediate movement is omitted? How explicit should the mode indicator be?
26. **Q26:** If performance eventually requires aggregate visitors beyond some scale, which individuals must stay fully simulated: selected guests, parties, injured guests, VIPs, employees, or all guests without exception?

**Art, density, and mountain activity.** The renderer can remain WebGL while the art stays 2D.

27. **Q27:** What should skiers look like: minimal dots, directional chevrons, tiny top-down people/skiers, or a mix by zoom? Name visual references you like.
28. **Q28:** Should markers scale with real-world size, stay readable in screen pixels, or interpolate between those behaviors?
29. **Q29:** At whole-mountain zoom, prefer moving people, subtle flow strokes, a density heatmap, or a quiet map with only selected activity? Should overlays be optional rather than automatic?
30. **Q30:** At 64x, should individuals stay densely visible with rapid event transitions, become sparse with optional flow, or automatically transition to flow summaries? **Pending async question.**
31. **Q31:** What matters more: exact visible headcount, believable visual crowding, or clear open space around infrastructure? Should the UI state how many real guests the visible sample represents?
32. **Q32:** How should queues appear: people arranged in a maze, compact clusters, a proportional length/area, or minimal terminal indicators? Is authoring queue footprints a future goal?
33. **Q33:** Do chairlifts need moving carrier icons in the first release? Must seats and guests visibly match each boarding, or is equipment-specific representative occupancy acceptable?
34. **Q34:** How much skiing personality is important: different line choice, turn size, speed, stopping, regrouping, falling, traversing, snowboarding, and equipment? Rank your top three.
35. **Q35:** Which color modes should exist: clothing variety, ability, activity, satisfaction, party, congestion, or incidents? Should colorblind palettes and reduced motion be first-release requirements?
36. **Q36:** Should the camera ever automatically zoom out, follow an incident, or leave a selected guest? Recommended default: preserve camera control and provide an explicit focus action.

**Guest and business simulation.** Preserving current behavior is the starting point; these answers prioritize deeper changes.

37. **Q37:** Which current guest behaviors feel wrong or too shallow? Include arrival timing, run selection, queues, crowding, needs, prices, spending, departure, and satisfaction.
38. **Q38:** Should a day's demand react only to information known before opening, or also to same-day weather, closures, queue news, and price changes? How much unpredictability is desirable?
39. **Q39:** Are displayed wait times expected waits, measured recent waits, an exact prediction for a particular guest, or all three clearly distinguished?
40. **Q40:** Should lift stops, loading efficiency, empty seats, singles lines, beginners, and families affect capacity in the first release or later?
41. **Q41:** Should terrain crowding change only guest satisfaction and route choice initially, or physically slow travel and influence incidents as well?
42. **Q42:** Should ticket/ancillary income remain tied to actual simulated visitors, or may some revenue be a documented aggregate estimate? Should season-pass cash and revenue recognition be separate views?
43. **Q43:** How much day-to-day memory should carry over: reputation, repeat visits, lodging stays, unmet demand, incidents, staff fatigue, and machine wear?
44. **Q44:** Which parts should be optional difficulty settings, and should changing those mid-save be allowed? Distinguish relaxing sandbox play from management challenge.

**Weather, snow, and long-term realism.** The repo already has pinned generated annual weather informed by historical data.

45. **Q45:** Does “true weather” mean the current deterministic synthetic climate based on historical data, exact replay of a historical year, or a choice? Should year-to-year surprises remain important?
46. **Q46:** Should fast-forward only expose forecast information available to the resort, or can a sandbox option know the exact next storm in advance?
47. **Q47:** How quickly should the player notice changes in snow conditions: by hour, by trail treatment/event, or continuously at fine spatial detail? Which visible changes matter most?
48. **Q48:** Rank future snow detail: base depth, coverage, surface quality, grooming age, traffic compaction, ice formation, moguls, pushed piles, water equivalent, and layered snow temperature.

**Automation and interruption.** Fast-forward needs useful policies and actions, not just warnings.

49. **Q49:** Should operations be manually controlled with optional automation, mostly automated with player policies, or scenario/difficulty dependent?
50. **Q50:** Which situations should pause, slow, notify, or remain silent: lift hold, bad cover, overcrowding, reservoir depletion, snowmaking opportunity, injury, job completion, and budget trouble?
51. **Q51:** Should protective actions such as wind holds and water shutoff happen automatically even if the player suppresses alerts? Which failures should be avoidable through judgment versus prevented by the system?
52. **Q52:** After resolving an alert, should time stay paused, resume the prior preset, or continue the pending advance-until target? Should the player choose per rule?
53. **Q53:** What must a monitoring view tell you to make skipping a week feel trustworthy? Name five numbers/trends, and the actions you would expect to take from them.
54. **Q54:** Can we place operational summaries and rules in existing dashboards and time/settings controls, or do you explicitly want a new permanent operations panel/ribbon? Repository guidance requires your explicit choice for new persistent divisions.
55. **Q55:** How much interruption is enjoyable in a busy day? Should minor repeats combine into a digest, and should pause/slow rules have user-set thresholds and cooldowns?
56. **Q56:** What can happen while you are constructing or editing infrastructure: normal simulation, automatic pause, or staged changes applied at a safe time? How should closures affect guests already on a lift or run?

**Grooming and future jobs.** We can preserve extension points without committing to all features now.

57. **Q57 — answered:** Assign equipment, routes, priorities, schedules, and automation rules; observe jobs progressing. Detailed direct vehicle control is not part of the selected foundation.
58. **Q58:** Which operation should be the first proof of the new foundation: grooming, snowmaking runtime, lift opening/maintenance, or patrol/sweep? What should the player do with it?
59. **Q59:** What makes grooming satisfying: visibly fresh corduroy, better guest experience, choosing priorities under time pressure, fleet/logistics optimization, or physically moving snow? Rank these.
60. **Q60:** Must grooming account for trail width/area, return travel, setup/turning, fuel, shifts, machine type, slope/winch access, partial treatment, repeated passes, and daylight guest conflicts? Separate first-release from eventual goals.

**Hardware, scale, and acceptable compromises.** Targets here decide whether exact fast-forward is sufficient.

61. **Q61 — partly answered:** Target **30 FPS** and **a few thousand visible guests on the mountain at once**. What minimum/target CPU, GPU, RAM, screen resolution, and device class should support that? Is the current Ryzen 5 5600X desktop representative?
62. **Q62:** Is the main target Windows Electron, static browser, or both equally? Must browser sessions resume exact individual guests and operations?
63. **Q63:** What are typical and maximum daily attendance, simultaneous guests, lift/trail counts, and mountain area? A 50,000-visitor day differs from 50,000 active guests at once.
64. **Q64:** Rank these under heavy load: smooth camera/input, precise individual simulation, immediate advance-through-season, and dense attractive visuals. Which may degrade first?
65. **Q65:** How long may an overnight skip, ordinary full-day skip, and whole-season skip take? Should long skips remain interactive or use a cancellable progress state?
66. **Q66:** Would you accept an explicitly labeled approximate fast-forward model if exact simulation cannot meet the target? Which differences are unacceptable even then: money, waits, injuries, daily visitors, satisfaction, or snow state?

**Compatibility and delivery.** These choices prevent an attractive prototype from creating an unreviewed save migration.

67. **Q67:** Existing saves: preserve old timing until season end and then offer conversion, convert a copy at a documented point with some state approximation, or make the first new model available only for new games?
68. **Q68:** AGENTS.md specifies new saves at schema 13, while the current code writes 16. Which is the intended current policy? Before implementation, should we prepare a migration proposal and then update the guidance to match the approved contract?
69. **Q69:** Which result should be delivered first: a speed/rendering feel prototype, correct full-calendar simulation, trustworthy fast-forward/alerts, or the first grooming/snowmaking job? How should we rank later stages?
70. **Q70:** What are three concrete “this is wrong” scenarios and three “this feels great” scenarios for the finished simulation? Examples are more useful here than broad realism claims.

**Recommended interview order.** Start with pacing and hardware, then resolve the guest-follow tradeoff and art direction, then automation and the first operational feature. Resolve migration before production calendar work. Use later rounds to turn broad answers into concrete acceptance scenes: the same crowded lift at several speeds; following a guest through lunch and a wind hold; an interrupted night shift; and advancing an ordinary week with a storm in the middle.
