# WCAG 2.1 AA Checklist — Mapped to your design system

## Perceivable

### 1.1 Text Alternatives
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 1.1.1 Non-text Content | All images, icons, controls have text alternatives | SVG icons need `aria-label` or `aria-hidden="true"` with visible text |

### 1.3 Adaptable
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 1.3.1 Info and Relationships | Structure conveyed visually is also in markup | Use semantic HTML (nav, main, header, section, h1-h6) |
| 1.3.2 Meaningful Sequence | Reading order matches visual order | Flex/grid order should match DOM order |
| 1.3.3 Sensory Characteristics | Instructions don't rely solely on shape/color/location | Don't say "click the blue button" — name the action |

### 1.4 Distinguishable
| Criterion | Requirement | your design system Token Mapping |
|-----------|-------------|---------------------------|
| 1.4.1 Use of Color | Color is not the only means of conveying info | Status tags include text labels, not just colored dots |
| 1.4.3 Contrast (Minimum) | Text: 4.5:1 ratio. Large text (18px+ or 14px bold): 3:1 | `--sds-text-primary` on `--sds-bg-page` = 15.4:1. `--sds-text-secondary` on white = 7.2:1. `--sds-text-tertiary` on white = 5.0:1 |
| 1.4.4 Resize Text | Text resizable to 200% without loss | Use relative units (rem/em) for font sizes |
| 1.4.11 Non-text Contrast | UI components: 3:1 against adjacent colors | `--sds-border-default` (#E0DCD3) on white = 1.5:1 — borders alone insufficient, use with background change |
| 1.4.13 Content on Hover | Hoverable content dismissible, hoverable, persistent | Tooltips must remain while hovered, dismiss with Escape |

## Operable

### 2.1 Keyboard Accessible
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 2.1.1 Keyboard | All functionality available via keyboard | Tab through nav items, Enter/Space to activate |
| 2.1.2 No Keyboard Trap | User can always Tab away from any component | Modals need focus trap WITH Escape to close |
| 2.1.4 Character Key Shortcuts | Single-character shortcuts can be remapped/disabled | Avoid single-key shortcuts; use modifier keys |

### 2.4 Navigable
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 2.4.1 Bypass Blocks | Skip navigation mechanism provided | Add "Skip to main content" link before header |
| 2.4.2 Page Titled | Pages have descriptive titles | Format: "Page Name — Section — App Name" |
| 2.4.3 Focus Order | Focus order matches logical reading order | Sidebar → content area → actions |
| 2.4.4 Link Purpose | Link purpose clear from text or context | "View details" → "View Talent Acquisition details" |
| 2.4.6 Headings and Labels | Headings describe topic or purpose | Use h1 for page title, h2 for sections, h3 for subsections |
| 2.4.7 Focus Visible | Focus indicator visible on all elements | `--sds-border-focus` (blue-750), 2px solid, 2px offset |

### 2.5 Input Modalities
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 2.5.1 Pointer Gestures | No multi-point or path-based gestures required | All interactions achievable with single click/tap |
| 2.5.2 Pointer Cancellation | Down-event doesn't trigger; use click/up event | Use `onclick`/`click` events, not `mousedown` |
| 2.5.3 Label in Name | Accessible name includes visible text | Button text = aria-label content |
| 2.5.4 Motion Actuation | No motion-triggered actions without alternative | N/A for most web apps |

## Understandable

### 3.1 Readable
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 3.1.1 Language of Page | `lang` attribute on `<html>` | `<html lang="en">` |
| 3.1.2 Language of Parts | `lang` on elements in different language | Mark up any non-English content |

### 3.2 Predictable
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 3.2.1 On Focus | Focus doesn't trigger context change | No auto-submit on focus |
| 3.2.2 On Input | Input doesn't auto-trigger unexpected changes | Select/radio changes filter, but don't navigate away |
| 3.2.3 Consistent Navigation | Navigation consistent across pages | Sidebar remains in same position/order |
| 3.2.4 Consistent Identification | Same function = same label everywhere | "Edit" always means "Edit", not sometimes "Modify" |

### 3.3 Input Assistance
| Criterion | Requirement | your design system Token Mapping |
|-----------|-------------|---------------------------|
| 3.3.1 Error Identification | Errors identified and described in text | Use `--sds-status-error-text` + descriptive message |
| 3.3.2 Labels or Instructions | Form inputs have labels | Always use `<label>` with `for` attribute |
| 3.3.3 Error Suggestion | Suggest corrections when possible | "Enter a valid email (e.g., name@company.com)" |
| 3.3.4 Error Prevention | Confirm destructive actions | Confirmation dialog for delete with danger button variant |

## Robust

### 4.1 Compatible
| Criterion | Requirement | your design system Notes |
|-----------|-------------|-------------------|
| 4.1.1 Parsing | Valid HTML | No duplicate IDs, proper nesting |
| 4.1.2 Name, Role, Value | Custom components expose name/role/value | Use ARIA roles for custom widgets: `role="tab"`, `role="tabpanel"`, `aria-selected` |
| 4.1.3 Status Messages | Status conveyed without focus change | Toast notifications use `role="status"` or `aria-live="polite"` |

## your design system Remediation Tokens

| Issue | Remediation Token |
|-------|-------------------|
| Missing focus style | `outline: 2px solid var(--sds-border-focus); outline-offset: 2px;` |
| Disabled element | `color: var(--sds-text-disabled); cursor: not-allowed;` |
| Error message | `color: var(--sds-status-error-text); background: var(--sds-status-error-bg);` |
| Success feedback | `color: var(--sds-status-success-text); background: var(--sds-status-success-bg);` |
| Warning indicator | `color: var(--sds-status-warning-text); background: var(--sds-status-warning-bg);` |
| Info message | `color: var(--sds-status-info-text); background: var(--sds-status-info-bg);` |
| Active navigation | `background: var(--sds-nav-item-active-bg); color: var(--sds-nav-item-active-text);` |
