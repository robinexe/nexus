# Laws of UX — Design Critique Reference

A comprehensive reference of 30 UX laws organized by critique relevance. Consult this during Standard and Deep critiques to ground your feedback in established research and psychology.

## Table of Contents
1. [Decision Making & Cognitive Load](#decision-making--cognitive-load)
2. [Visual Perception & Grouping (Gestalt)](#visual-perception--grouping-gestalt)
3. [Attention & Memory](#attention--memory)
4. [Interaction & Performance](#interaction--performance)
5. [User Expectations & Mental Models](#user-expectations--mental-models)
6. [Design Philosophy & Strategy](#design-philosophy--strategy)

---

## Decision Making & Cognitive Load

These laws govern how users process information and make choices. Violations lead to confusion, abandonment, and decision paralysis.

### Hick's Law
**Decision time increases with the number and complexity of choices.**

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Dense navigation with 15+ top-level items | Too many choices slow decisions | Group into categories, use progressive disclosure |
| Settings page with every option visible at once | Cognitive overload | Organize into tabs/sections, show advanced settings separately |
| Multiple CTAs competing on one screen | User freezes, unsure what to click | One primary CTA per view, demote secondary actions visually |
| Onboarding that asks too much upfront | Users abandon | Progressive onboarding — reveal features over time (Slack's bot approach) |

**Caveat:** Don't oversimplify to the point of abstraction. Hiding everything behind menus creates a different problem (discoverability). The goal is reducing *unnecessary* choices, not all choices.

### Choice Overload
**People get overwhelmed when presented with too many options.**

Closely related to Hick's Law but focuses on the emotional/motivational impact: users who face too many options often choose nothing at all, or feel less satisfied with whatever they pick.

| What to Look For | Fix |
|-----------------|-----|
| Product listing with 50+ unfiltered items | Add filtering, sorting, and comparison tools |
| Dropdown with 30+ undifferentiated options | Group options, add search/type-ahead, highlight recommended |
| Pricing page with 6+ tiers | Limit to 3-4 tiers, highlight recommended, add comparison table |

### Cognitive Load
**The amount of mental resources needed to understand and interact with an interface.**

Two types matter for critique:
- **Intrinsic load**: The inherent difficulty of the task (can't be designed away, but can be supported)
- **Extraneous load**: Mental effort caused by bad design (this is what you're looking for in a critique)

| Extraneous Load Source | Example | Fix |
|----------------------|---------|-----|
| Unclear labels or jargon | "Configure SAML IdP" | Plain language: "Set up single sign-on" |
| Inconsistent patterns | Different table sorting behaviors across pages | Standardize interactions |
| Unnecessary steps | 5-click flow that could be 2 clicks | Eliminate redundant confirmations and intermediate screens |
| Visual clutter | 8 different font sizes on one page | Establish clear typographic hierarchy |

### Miller's Law
**The average person can hold 7 (plus or minus 2) items in working memory.**

This is commonly misapplied. It does NOT mean "limit menus to 7 items." Visible menus use recognition (not recall), so they work fine with more items. Miller's Law applies when users must *remember* information — not when they can see it.

| Applies When | Doesn't Apply When |
|--------------|-------------------|
| User must remember an ID from screen A to enter on screen B | User is scanning a visible list of 20 items |
| Form asks for data the user needs to recall | Navigation menu has 12 clearly labeled items |
| Multi-step wizard where prior inputs aren't visible | Dashboard with many visible data cards |

### Working Memory
**Holds 4-7 chunks of information for 20-30 seconds before fading.**

| What to Look For | Fix |
|-----------------|-----|
| User must remember values across screens | Carry context forward — show summary, use breadcrumbs |
| Comparison requires scrolling back and forth | Provide side-by-side comparison view |
| Instructions shown once then hidden | Keep reference information accessible, use contextual help |

### Chunking
**Breaking information into smaller, meaningful groups improves processing.**

| What to Look For | Fix |
|-----------------|-----|
| Wall of unbroken text | Break into short paragraphs with clear headings |
| Long unformatted data strings (phone, credit card, IDs) | Format with separators: `4111 1111 1111 1111` not `4111111111111111` |
| Dense table with no visual grouping | Add section headers, alternating backgrounds, or card-based grouping |
| Form with 20 fields in one section | Group related fields under clear headings |
| Video/tutorial with no chapters | Break into discrete, navigable sections |

**Key formatting patterns:**
- Lines of 50-75 characters for optimal scanning
- Bulleted lists over long paragraphs
- Contrasting headings and keywords
- White space between logical groups

### Cognitive Bias
**Systematic errors in thinking that influence perception and decision-making.**

Not a single law but a family of biases that affect how users interpret interfaces. The most relevant for design critique:

| Bias | Design Impact |
|------|--------------|
| **Confirmation bias** | Users see what they expect — if your design looks like a pattern they know, they'll interact with it that way even if it works differently |
| **Anchoring** | The first piece of information users see disproportionately influences their judgment (impacts pricing, data presentation) |
| **Status quo bias** | Users resist changes to familiar interfaces even when the new design is objectively better |

---

## Visual Perception & Grouping (Gestalt)

These laws describe how users perceive visual relationships between elements. They're fundamental to layout critique.

### Law of Proximity
**Objects near each other are perceived as belonging together.**

This is one of the most commonly violated Gestalt principles. Spacing communicates relationships more powerfully than labels.

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Label is equidistant between two fields | Ambiguous association | Label should be closer to its field than to adjacent fields |
| Related actions are far apart | Users miss the connection | Group related actions together |
| Unrelated items are too close | Users assume false relationship | Add whitespace between unrelated groups |
| Card content with uniform spacing | No internal hierarchy | Use tighter spacing within groups, wider spacing between groups |

### Law of Similarity
**Similar-looking elements are perceived as related.**

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Clickable and non-clickable text look the same | Users can't tell what's interactive | Links use distinct color (`--sds-text-link`), buttons have distinct styling |
| Different element types share the same styling | Users assume they work the same way | Visually differentiate elements that behave differently |
| Navigation items mix styles arbitrarily | Users can't parse the structure | Consistent styling for same-level items |

### Law of Common Region
**Elements sharing a boundary are perceived as grouped.**

| What to Look For | Fix |
|-----------------|-----|
| Related content with no visual container | Add card container, subtle background, or border |
| Too many nested containers | Reduce nesting; use spacing instead of borders where possible |
| Inconsistent container usage | Same types of content groups should use same container treatment |

### Law of Uniform Connectedness
**Visually connected elements are perceived as more related than disconnected elements.**

| Pattern | Application |
|---------|-------------|
| Lines connecting steps in a flow | Step indicators, timelines, progress flows |
| Shared background color | Grouping related filter options, nav sections |
| Borders around related results | Featured snippets, video results, card groups |

### Law of Pragnanz (Simplicity)
**People interpret complex images as the simplest form possible.**

Users will simplify what they see. If your layout is ambiguous, they'll misread it by picking the simplest interpretation — which may not be what you intended.

| What to Look For | Fix |
|-----------------|-----|
| Ambiguous layout that could be read multiple ways | Clarify grouping with proximity, alignment, and containers |
| Complex visualizations without clear reading order | Add labels, annotations, and guided hierarchy |
| Decorative elements that create false groupings | Remove or reposition decoration that confuses visual parsing |

---

## Attention & Memory

These laws govern what users notice and remember. Critical for critique of information hierarchy and notification design.

### Selective Attention
**People focus on goal-relevant stimuli and filter out the rest.**

Two important sub-phenomena:

**Banner Blindness:** Users automatically ignore content that *looks like* advertising — positioned at the top of the page, in the right rail, with colorful/rectangular/animated styling, or adjacent to actual ads.

| What to Look For | Fix |
|-----------------|-----|
| Important content styled like an ad (colorful banner, rectangular, animated) | Match your site's content design language — don't make critical content look promotional |
| Key information in the right rail | Test placement; right rail is a blindness zone |
| Legitimate content adjacent to ads | Separate content and ads into distinct visual regions |

**Change Blindness:** Users miss changes to the interface, especially when changes coincide with visual interruptions (page reloads, scrolling, blinks).

| What to Look For | Fix |
|-----------------|-----|
| Error messages that appear after a page reload with no visual emphasis | Use contrast, color, animation to draw attention; position near the user's focus |
| Filter results that update instantly with no feedback | Animate the transition or show a "results updated" indicator |
| Status changes in peripheral vision | Use the "squint test" — if you can't spot the change while squinting, users won't notice it |

### Serial Position Effect
**Users best remember the first and last items in a series.**

| What to Look For | Fix |
|-----------------|-----|
| Most important nav items buried in the middle | Place key actions at the start and end of navigation bars |
| Critical information in the middle of a long list | Promote to the top or use visual emphasis |
| Onboarding with the key step in the middle | Put the most impactful step first or last |

### Von Restorff Effect (Isolation Effect)
**The item that differs visually from the rest is most likely remembered.**

| What to Look For | Fix |
|-----------------|-----|
| Primary CTA doesn't stand out from secondary actions | Give the primary action a distinct color, size, or treatment |
| Everything is visually "loud" — nothing stands out | Mute secondary elements so the important thing pops |
| Color is the only differentiator | Add size, weight, or icon differences — don't rely solely on color (accessibility) |
| Overuse of emphasis | When everything is bold/colored, nothing stands out — be selective |

### Zeigarnik Effect
**People remember incomplete tasks better than completed ones.**

| What to Look For | Application |
|-----------------|-------------|
| Multi-step flows with no progress indicator | Add progress bar — users are motivated to complete what they've started |
| Profile setup with no completion percentage | Show "Your profile is 60% complete" to drive engagement |
| Onboarding with no sense of progress | Show steps completed vs. remaining |

**Related: Goal-Gradient Effect** — users accelerate effort as they approach completion. Showing progress near the finish line is especially motivating. Artificial head starts ("You've already completed step 1!") boost completion rates.

---

## Interaction & Performance

These laws govern how users physically interact with interfaces and their expectations around responsiveness.

### Fitts's Law
**The time to reach a target depends on its distance and size.**

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Tiny click targets (< 44px on touch) | Hard to tap, high error rate | Minimum 44x44px touch targets (Apple HIG), 48x48px (Material) |
| Important buttons far from the user's cursor/finger position | Slow interaction | Place primary actions near where the user's attention already is |
| Interactive targets packed tightly together | Accidental clicks on wrong target | Add adequate spacing (minimum 8px between targets) |
| Modal actions far from the trigger | User must traverse the screen to confirm | Position confirmation buttons near the trigger action |

### Doherty Threshold
**Productivity soars when system response time is under 400ms.**

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Clicks with no response for > 400ms | Interface feels broken or sluggish | Add immediate visual feedback (optimistic UI, skeleton screens) |
| Slow transitions/animations (> 400ms) | User waits, flow breaks | Keep transitions 150-300ms |
| Progress indicators that don't move | User thinks the system is frozen | Use animations, percentage, or time estimates |

**Nuance:** Strategic delays can *increase* perceived value. If a search returns instantly, users may doubt its thoroughness. A brief artificial delay (200-400ms) with a loading animation can make results feel more considered.

### Flow
**The state of full immersion where challenge matches skill.**

| What to Look For | Problem | Fix |
|-----------------|---------|-----|
| Expert users forced through unnecessary confirmations | Boredom, frustration | Reduce friction for power users (keyboard shortcuts, bulk actions) |
| Novice users dropped into a complex interface | Anxiety, overwhelm | Guided onboarding, progressive disclosure |
| Interruptions in the middle of focused tasks | Flow breaks | Minimize modals, notifications, and interruptions during active workflows |
| No feedback on progress or achievement | No sense of accomplishment | Show completion indicators, success states |

### Goal-Gradient Effect
**Effort increases as users approach a goal.**

| What to Look For | Fix |
|-----------------|-----|
| Multi-step flow with no sense of progress | Add step indicators showing current position and total steps |
| Progress bars that don't reflect actual progress | Ensure progress mapping is accurate and moves visibly |
| No artificial head start | Show "Step 1 of 5 complete" even at the start to create momentum |

---

## User Expectations & Mental Models

These laws govern how users expect interfaces to behave based on prior experience.

### Jakob's Law
**Users prefer your product to work like the ones they already know.**

This is the most important law for enterprise product design. Users bring expectations from Google, Microsoft, Salesforce, and consumer tools.

| What to Look For | Violation | Fix |
|-----------------|-----------|-----|
| Non-standard navigation patterns | Users can't find features | Follow established sidebar/topbar patterns |
| Custom controls when standard ones exist | Users don't know how to use them | Use standard form controls, table patterns, modal patterns |
| Unique terminology for common concepts | Users must learn your vocabulary | Use industry-standard terms |
| Redesign that changes everything at once | Users feel lost | Allow gradual transition, offer access to familiar version |

### Mental Model
**Users carry compressed models of how systems work, built from prior experience.**

| What to Look For | Problem | Fix |
|-----------------|---------|-----|
| Interface behaves differently than it looks | Mental model mismatch — user expects one thing, gets another | Align behavior with visual affordances |
| Feature works differently than the same feature in competitor products | Expectation violation | Research how competitors implement the same concept |
| Toggle that looks like a toggle but behaves like a radio button | Confusion and distrust | Use the correct component for the behavior |

### Paradox of the Active User
**Users never read manuals — they start using the software immediately.**

| What to Look For | Fix |
|-----------------|-----|
| Features that require reading documentation to use | Make features self-explanatory through clear labels, affordances, and inline guidance |
| Onboarding manuals or lengthy tutorials | Replace with contextual, in-context tips triggered by user behavior |
| Error messages that assume users read the docs | Include the relevant information inline |

### Postel's Law (Robustness Principle)
**Be liberal in what you accept, conservative in what you send.**

| What to Look For | Fix |
|-----------------|-----|
| Rigid input validation (rejects "555 123 4567" because it wants "5551234567") | Accept flexible formats, reformat behind the scenes |
| Form that rejects valid input due to strict formatting | Auto-format, strip whitespace, handle edge cases |
| System that shows raw technical output to users | Clean, format, and humanize all system output |

---

## Design Philosophy & Strategy

These laws guide higher-level design decisions and trade-offs.

### Aesthetic-Usability Effect
**Users perceive attractive designs as more usable — even when they're not.**

This is a double-edged sword: visual polish buys forgiveness for minor issues, but it can also mask real usability problems during testing.

| What to Look For | Implication |
|-----------------|-------------|
| Beautiful interface that users praise but struggle with | Don't let aesthetics mask usability problems — observe behavior, not just sentiment |
| Functional but visually rough interface | Polish matters — users will judge usability more harshly |
| Usability test where participants say "it's fine" but their behavior shows struggle | Probe deeper — the aesthetic-usability effect may be hiding real issues |

### Occam's Razor
**The simplest solution is usually the best.**

| What to Look For | Fix |
|-----------------|-----|
| Over-engineered UI with unnecessary complexity | Remove components that don't serve the user's goal |
| Multiple ways to do the same thing that confuse rather than help | Consolidate to one clear path |
| Features added "just in case" | Remove until something is missing, not until everything is present |

### Tesler's Law (Conservation of Complexity)
**Every system has irreducible complexity. The question is who bears it — the system or the user.**

| What to Look For | Fix |
|-----------------|-----|
| Complex configuration that could be automated | Move complexity into the system — use smart defaults, auto-detection |
| User must make decisions the system could make for them | Provide intelligent defaults with option to override |
| Wizard that oversimplifies and removes necessary control | Don't remove complexity users need — absorb the right complexity, not all of it |

### Pareto Principle (80/20 Rule)
**~80% of effects come from ~20% of causes.**

| Application to Critique |
|------------------------|
| Identify the 20% of features users interact with most — these deserve the most design attention |
| Focus critique on the primary user flows, not edge-case screens |
| When suggesting improvements, prioritize the changes that will impact the most users |

### Peak-End Rule
**People judge experiences by their most intense moment and the ending.**

| What to Look For | Fix |
|-----------------|-----|
| Frustrating peak moments (complex forms, confusing errors) | Smooth the worst parts of the experience |
| Abrupt or anticlimactic endings (form submitted with no confirmation) | Design satisfying completion states — confirmation, next steps, celebration |
| No positive peak moments | Create moments of delight at key interaction points |
| Error pages that are generic and unhelpful | Make error states informative and even memorable (the "Fail Whale" approach) |

### Parkinson's Law
**Tasks expand to fill the time available.**

| What to Look For | Fix |
|-----------------|-----|
| Forms that feel longer than they need to be | Use autofill, smart defaults, and pre-population to speed completion |
| Flows with no time pressure or progress feedback | Show estimated completion time or step count |
| Tasks that could be streamlined with better defaults | Pre-fill everything the system already knows |
