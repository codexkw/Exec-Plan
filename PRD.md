# EXECPLAN — Product Requirements Document (PRD)

**Operational Plan Activation & Live Execution Tracking**
نظام إكسبلان — تفعيل الخطط ومتابعة التنفيذ لحظة بلحظة

---

## Document Control

| Field | Value |
|---|---|
| Product | EXECPLAN |
| Document | Product Requirements Document (PRD) |
| Version | 1.1 (re-platform baseline) |
| Date | June 2026 |
| Status | Draft for build — consolidates MVP 1.0 reports into a single requirements baseline. **Rev 1.1:** real-time transport changed from polling to SignalR/WebSockets (NFR-1, §13.1). |
| Region | Kuwait / GCC |
| Language | Arabic-first (RTL); complete English locale |
| Target platform | .NET 10 backend + ASP.NET Core MVC/Razor web; Flutter mobile; SQL Server |
| Source inputs | Business Model Report v1.0; Features & Requirements Spec MVP 1.0; Technical Report MVP 1.0 |

**Purpose of this document.** This PRD consolidates the three source reports into one authoritative requirements baseline and governs the re-platform of EXECPLAN from its original Django / Next.js / Expo MVP onto a new stack: a .NET 10 backend and ASP.NET Core MVC/Razor web application, a Flutter mobile application, and SQL Server. The *product* requirements (what the system does and the rules it enforces) are carried forward unchanged from the verified MVP; the *platform* requirements (how it is built) reflect decisions taken for this re-build and are called out explicitly in Section 17.

---

## 1. Executive Summary

EXECPLAN converts an operational plan into executable tasks, assigns them to teams resolved from a live shift roster, activates the plan in one tap, and tracks execution on a live dashboard. It is deliberately **not** a general project-management tool. The entire product is organized around a single cycle:

> **Create Plan → Activate → Notify People → Execute Tasks → Live Dashboard**

Two mechanics distinguish it from a checklist or alerting app:

1. **The readiness tap is the only definition of "responded."** A participant tapping «أنا جاهز» ("I'm ready") is the one and only acknowledgment that counts. Opening the app, viewing tasks, or completing tasks do not count.
2. **Disciplined substitute escalation.** A non-responder who ignores a configurable number of call attempts (default five) is automatically replaced by a pre-designated substitute, who instantly receives their own task list, notification, and first call attempt.

The dashboard answers the three questions an operations chief actually asks during an activation: **who has mobilized, which teams are ahead or behind, and what is overdue.**

The product is **Arabic-first by construction** — right-to-left throughout, with labels written for field staff rather than translated, and Kuwait timezone and shift conventions native to the domain logic — while shipping a complete English locale for mixed workforces.

A working MVP (backend, web, mobile) is complete and verified end-to-end. This PRD governs re-platforming that proven design onto .NET + Flutter without altering its behavior.

---

## 2. Problem Statement & Background

Organizations that run operational plans — emergencies, guard rotations, maintenance windows, inspection campaigns, transport operations — still activate them with phone trees, WhatsApp groups, and paper checklists. The failure modes are predictable and expensive:

- Nobody knows in real time who has actually mobilized.
- Tasks silently stall because the assignee never saw them.
- Substitutes are called too late, or not at all.
- After the event there is no defensible record of who responded, when, and what got done.

In safety-critical contexts such as municipal storm response or facility emergencies, the gap between "the plan was activated" and "the right people are working the right tasks" is measured in **minutes that matter**.

**Why existing tools do not solve this.** Generic project-management tools assume desk workers planning weeks of work, not shift workers who must confirm readiness in seconds from a phone. Mass-notification platforms solve the alerting half but stop at "message delivered" — they do not generate tasks, track execution, rank teams, or escalate to substitutes. EXECPLAN's thesis is that **the activation cycle is a product of its own.**

---

## 3. Goals, Objectives & Success Metrics

### 3.1 Product goals
- Make mobilization **fast, visible, and auditable**: one-tap activation, one-tap readiness, one live dashboard.
- Guarantee **continuity** through automatic substitute escalation when people don't respond.
- Produce a **defensible timestamped record** of every notification, call attempt, response, escalation, and task completion.
- Serve **non-technical field staff** in Arabic with a one-primary-action-per-screen design.
- Run on **modest infrastructure** (a single server per tenant), suitable for closed municipal/emergency networks.

### 3.2 Business objectives
- Establish a Kuwait beachhead and expand across the GCC.
- Convert paid pilots into reference customers and annual contracts.
- Monetize notification channels as metered usage and offer an on-premises licensing tier for government data-sovereignty requirements.

### 3.3 Success metrics (KPIs)
In priority order (targets to be set against pilot feedback):

| Metric | Why it matters |
|---|---|
| Pilot-to-paid conversion rate | Proves commercial viability of the model |
| **Time-to-mobilization improvement** (measured in drills) | The product's headline ROI number |
| Activations per customer per month | Engagement, and the driver of metered channel revenue |
| Net revenue retention (NRR) | Expansion as customers add departments and plans |
| Public-sector tender win rate (with reference customers) | Government go-to-market traction |

A **drill** — a room watching the dashboard fill with green as field staff tap readiness — is both the primary acceptance demonstration and the most persuasive sales artifact the product can produce.

---

## 4. Product Vision & Guiding Principles

These principles are **binding on all features**. Any proposed feature that violates one is out of scope by default.

1. **One cycle, not a toolkit.** Every feature maps onto Create → Activate → Notify → Execute → Dashboard. Anything outside the cycle is out of scope for the MVP.
2. **Template versus snapshot.** A plan is a reusable template; activating it freezes an immutable snapshot. Editing a plan never disturbs a running activation.
3. **One definition of "responded."** A response is the participant tapping the readiness button «أنا جاهز». Nothing else counts.
4. **Built for non-technical field staff.** Big buttons, one primary action per screen, minimal text, clear Arabic labels, no jargon.
5. **Arabic-first.** Right-to-left throughout; Kuwait timezone and shift conventions native; a complete English locale ships alongside.
6. **Operationally lean.** Runs on a single modest server: real-time updates are served by an **in-process SignalR hub** (no separate message broker or background-job infrastructure required at single-instance scale).

---

## 5. Target Market, Segments & Competitive Landscape

### 5.1 Market and segments
The beachhead is **Kuwait**, expanding across the **GCC**. Primary segments, in order of fit:

1. **Municipal & government operations** — storm/flood response, public-works mobilization, event security plans.
2. **Facilities & property management** groups running guard, maintenance, and inspection rotations across sites.
3. **Utilities and oil-and-gas contractors** with shift-based field crews and strict emergency-response obligations.
4. **Hospitals and large campuses** with code-activation procedures.

Each already owns written plans and shift rosters — EXECPLAN **digitizes assets they already have** rather than asking them to invent new process. Bottom-up sizing (a planning assumption, not market research): plausibly **150–300 organizations** in Kuwait match the ideal customer profile, with the wider GCC several times larger.

### 5.2 Buying pattern
Direct, relationship-driven sales into government and large enterprises, often through **local system-integrator (SI) partners**, with tenders that reward **Arabic-language capability, local/on-premises hosting, and demonstrable working software**. The installable MVP is itself a sales asset.

### 5.3 Competitive landscape
| Competitor type | Strength | Gap EXECPLAN exploits |
|---|---|---|
| Mass-notification / critical-event platforms (e.g., Everbridge, AlertMedia) | Strong alerting | No task execution, ranking, or substitute escalation; not Arabic-first; priced for Western enterprises |
| Generic work-management tools (Asana, Monday, Trello) | Flexible checklists | No shift rosters, activation snapshots, readiness acknowledgment, or escalation; overwhelm field staff |
| **Status quo** — WhatsApp groups & phone calls | Free, familiar | No audit trail, no escalation automation, no dashboard |

**Defensible position:** the intersection of activation-cycle mechanics (which notification platforms lack), operational simplicity (which PM tools lack), and Arabic-first regional fit (which both lack).

---

## 6. User Roles & Personas

Four roles are recognized. Each determines the home screen, available actions, and the slice of data the user can see.

| Role | Arabic | Platform | Responsibilities |
|---|---|---|---|
| **System Admin** | مدير النظام | Web | Manage organizations, departments, and users; assign roles; full visibility across the system. |
| **Plan Manager** | مدير الخطة | Web + Mobile | Create/edit plans; define teams, tasks, and shift rosters; activate plans; watch the live dashboard; broadcast; run escalation; close activations. |
| **Team Leader** | قائد الفريق | Mobile-first | See their team; assign plan tasks to members; set a substitute per member; monitor team progress; raise issues to the plan manager. |
| **Team Member** | عضو الفريق | Mobile | Receive activation notification; acknowledge readiness; see only their own tasks; mark tasks done; add a note; read broadcasts. |

---

## 7. Scope

### 7.1 In scope (MVP 1.0 — carried forward to the re-platform)
The seven functional modules in Section 8, the business rules in Section 10, the 15-entity data model in Section 11, the API surface in Section 12, all **MUST** and the delivered **SHOULD** requirements, Arabic-first localization with English locale, and the role-based security model.

### 7.2 Out of scope (deferred roadmap)
Each deferred item lands on a seam the MVP deliberately left open and requires **no re-architecture**:

- Real notification channels (voice, SMS, WhatsApp) behind the existing provider interface.
- Automatic **timed** escalation via a scheduler (MVP escalation is manually triggered).
- Automatic **shift handover** when an activation crosses a shift boundary.
- Surveys, lessons-learned, and the full post-activation **FinalReport** module (MVP closure shows counts only).
- Checklist granularity below the task level.
- Permission granularity beyond the four roles.
- **Mobile OS push notifications** (FCM / APNs) for delivery while the app is backgrounded or closed. *(In-app real-time updates via SignalR are in scope; OS-level push to a closed app is the deferred item.)*
- **Horizontal scale-out** of the real-time hub across multiple server instances (would add a Redis/SQL Server backplane); single-instance in-process SignalR is the MVP target.

---

## 8. Functional Requirements — by Module

Requirements use **MoSCoW** priority. All **MUST** items are part of the delivered baseline.

### 8.1 Authentication & Identity
- Username/password sign-in issuing JWT access and refresh tokens.
- Automatic, transparent access-token refresh; expired sessions return the user to login.
- Persistent session on mobile (secure on-device storage) and web (browser storage).
- Role-based routing to the correct home screen immediately after login.

| ID | Priority | Requirement |
|---|---|---|
| FR-AUTH-1 | MUST | Authenticate users by username and password and issue JWT access and refresh tokens. |
| FR-AUTH-2 | MUST | Refresh an expired access token automatically using the refresh token, without forcing re-login. |
| FR-AUTH-3 | MUST | On invalid or expired credentials, return the user to the login screen. |
| FR-AUTH-4 | MUST | Route each user to a role-appropriate home screen after sign-in. |

### 8.2 Organization & User Administration
| ID | Priority | Requirement |
|---|---|---|
| FR-ADM-1 | MUST | An admin shall create and list organizations and departments. |
| FR-ADM-2 | MUST | An admin shall create users with a role, name, phone, and department, and view all users in a table. |
| FR-ADM-3 | MUST | All user/department/organization write operations shall be restricted to the admin role. |
| FR-ADM-4 | SHOULD | A plan manager shall read the user list (read-only) to populate team membership during plan authoring. |

### 8.3 Plan Authoring
- Create a plan with name, type (nine categories: daily, weekly, emergency, guard, transport, maintenance, IT, inspection, general), objective, description, scope.
- Store important contacts and emergency numbers as structured name/number lists.
- Define teams within a plan, each with an optional team leader; assign members to teams.
- Define task templates per team (title, order, duration).
- Build a shift roster: assign people to a team for a shift and date, with an optional substitute per person.
- A **four-step guided creation wizard** with validation at each step: *معلومات الخطة · الفرق والأعضاء · المهام · النوبات والمراجعة* (Plan details · Teams & members · Tasks · Shifts & review).
- Designate which users are authorized to activate the plan.

| ID | Priority | Requirement |
|---|---|---|
| FR-PLAN-1 | MUST | Create a plan with name, type, objective, description, and scope. |
| FR-PLAN-2 | MUST | Store important contacts and emergency numbers as structured name/number lists. |
| FR-PLAN-3 | MUST | Define teams within a plan and assign members to each team. |
| FR-PLAN-4 | MUST | Define task templates per team, each with a title, ordering, and duration. |
| FR-PLAN-5 | MUST | Build a shift roster assigning users to a team for a given shift and date, optionally designating a substitute per person. |
| FR-PLAN-6 | MUST | The creation wizard shall present exactly four steps and validate each before advancing. |
| FR-PLAN-7 | MUST | Designate which users are authorized to activate the plan. |
| FR-PLAN-8 | MUST | Editing a plan shall never alter any existing activation derived from it. |

### 8.4 Activation
- One-action activation behind a plain-language confirmation.
- Immutable snapshot: participants resolved from the current shift roster, tasks generated from templates, team names frozen as text.
- Per-participant in-app notification and first call attempt recorded at activation.
- Guards: refuses to activate if a plan is already running or if no one is on duty for the current shift; restricted to authorized users.

| ID | Priority | Requirement |
|---|---|---|
| FR-ACT-1 | MUST | Activation shall create an immutable snapshot recording participants, tasks, and team names at that instant. |
| FR-ACT-2 | MUST | Activation shall resolve participants from the shift roster for the current shift and date. |
| FR-ACT-3 | MUST | Activation shall generate one execution task per participant per applicable task template, with a due time derived from the template duration. |
| FR-ACT-4 | MUST | Activation shall record one in-app notification and one initial call attempt per participant. |
| FR-ACT-5 | MUST | Activation shall be rejected if the plan already has an active run, or if no one is on duty for the current shift. |
| FR-ACT-6 | MUST | Activation shall be permitted only to the plan creator, its authorized activators, or an admin. |

### 8.5 Execution & Response
| ID | Priority | Requirement |
|---|---|---|
| FR-EXE-1 | MUST | A member shall acknowledge readiness with a single action; this is the only event counted as a response. |
| FR-EXE-2 | MUST | A member shall mark tasks done/not-done and add a note per task. |
| FR-EXE-3 | MUST | A member shall see only their own tasks and only activations they participate in. |
| FR-EXE-4 | MUST | A team leader shall reassign a task only to a member of a team they lead. |
| FR-EXE-5 | MUST | A team leader shall set or change a participant's substitute on a live activation. |
| FR-EXE-6 | SHOULD | A team leader shall raise an issue to the plan manager, recorded and shown in the dashboard event log. |

### 8.6 Escalation
| ID | Priority | Requirement |
|---|---|---|
| FR-ESC-1 | MUST | An escalation cycle shall be triggerable from the dashboard **and** from a server management command, with identical behavior. |
| FR-ESC-2 | MUST | Each cycle shall add one call attempt for every participant still pending. |
| FR-ESC-3 | MUST | On reaching the configured attempt threshold without a response, a participant shall be marked escalated and their substitute inducted with tasks, notification, and a first call attempt. |
| FR-ESC-4 | MUST | The escalation threshold shall be a configurable setting (default five). |

### 8.7 Live Monitoring, Broadcast & Closure
| ID | Priority | Requirement |
|---|---|---|
| FR-MON-1 | MUST | A single aggregated monitoring payload shall be computed server-side and delivered to subscribed clients **in real time over a SignalR hub**. A REST endpoint shall return the same snapshot on initial load and serve as a fallback if the hub connection drops. |
| FR-MON-2 | MUST | The dashboard shall present the five participant counters, response and task-completion rates, per-team ranking, overdue tasks, and the latest 50 events. |
| FR-MON-3 | MUST | Per-team rows shall be sorted best-to-delayed to yield "best team" and "delayed team" indicators. |
| FR-MON-4 | MUST | A manager shall broadcast a message to all participants; members shall read broadcasts in-app. |
| FR-MON-5 | MUST | A manager shall close an activation; the system shall present a final counts summary and stop live updates (clients are unsubscribed from the hub). |

---

## 9. Key User Journeys

### 9.1 Manager: create and activate a plan
1. Sign in → routed to **My Plans** (خططي).
2. Launch the **four-step wizard**: plan details → teams & members → task templates → shift roster & review (each step validated in Arabic).
3. Designate authorized activators; save the plan as a reusable template.
4. Open **Plan Detail**; press the oversized **«إطلاق الخطة»** (Activate) button behind a plain-language confirmation.
5. System resolves on-duty participants for the current shift/date, freezes the snapshot, generates tasks, sends notifications + first call attempts.
6. Manager lands on the **Live Dashboard**.

### 9.2 Member: acknowledge and execute
1. Receive activation notification → open **Member Home**.
2. Tap **«أنا جاهز ✅»** — the only action that registers a response.
3. Work the personal task checklist: mark done/undo, add a note per task.
4. Read manager broadcasts in the in-app inbox.

### 9.3 Leader: manage the team
1. Open **Leader Home** → team progress.
2. Reassign a task to another member **of a led team only**.
3. Set/override a participant's substitute on the live activation.
4. Raise an issue to the manager (surfaces in the dashboard event log).

### 9.4 Escalation & closure
1. Manager (or a scheduled command, future) triggers an **escalation cycle**: +1 call attempt per pending participant.
2. At the threshold (default 5), the non-responder is marked **escalated** and their frozen substitute is **inducted** with full tasks, notification, and call attempt #1.
3. Manager **broadcasts** as needed and finally **closes** the activation → final counts summary; live updates stop and clients are unsubscribed from the hub.

---

## 10. Business Rules & Logic

| Rule | Definition |
|---|---|
| **Shifts** | Three fixed shifts in **Asia/Kuwait**: Morning 06:00–14:00, Evening 14:00–22:00, Night 22:00–06:00. |
| **Night roster date** | A night shift in progress **after midnight** resolves to the **previous calendar day's** roster, since the 22:00–06:00 shift belongs to the day it began. |
| **On-duty resolution** | At activation, for each required team the system reads who is assigned to the current shift today; that person is on duty and their substitute chain is **frozen** onto the participant record. |
| **Shift-boundary** | If an activation crosses a shift boundary, participants remain those frozen at activation time. Automatic handover is a future feature. |
| **Response** | A response equals the readiness tap. Opening the app, viewing tasks, or completing tasks **do not** count. |
| **Escalation** | Non-responder → one call attempt per cycle → at threshold (default 5) marked escalated → substitute inducted with full task set, notification, and first call attempt. |
| **Substitute source** | The substitute is resolved from the roster's substitute link and frozen at activation; a leader may override it on a live activation, affecting subsequent cycles. |
| **Notifications/calls (MVP)** | Recorded as database entries only; **no external delivery**. A provider interface isolates future SMS/voice/WhatsApp integration. |

---

## 11. Data Model & Entities

**Fifteen entities** implement the MVP, divided between the **template side** and the **runtime snapshot side**. The central design decision is the separation of **Plan** (reusable template) from **PlanActivation** (immutable runtime snapshot): activation copies what matters at that instant, so editing a plan afterwards can never disturb a running activation.

| Entity | Side | Role in the system |
|---|---|---|
| **User** | — | Custom account with role, phone, organization, department. |
| **Organization / Department** | — | Organizational hierarchy. |
| **Plan** | Template | Reusable template: metadata, contacts, authorized activators, status. |
| **Team / TeamMembership** | Template | Teams within a plan and their member assignments. |
| **ShiftAssignment** | Template | Roster row (user, team, shift, date, substitute-for) — answers "who is on duty". A non-null `substitute-for` marks the row as someone's designated substitute. |
| **TaskTemplate** | Template | Task blueprint per team (title, order, duration). |
| **PlanActivation** | Runtime | Immutable runtime snapshot (status, shift, timestamps). |
| **ActivationParticipant** | Runtime | A person frozen into an activation, with status and resolved substitute. |
| **ExecutionTask** | Runtime | A task instance owned by a participant (status, note, due time). |
| **NotificationLog** | Runtime | Record of an in-app notification or broadcast to a recipient. |
| **CallAttempt** | Runtime | Numbered call-attempt placeholder per participant. |
| **ResponseStatus** | Runtime | Marks that a participant acknowledged readiness, with timestamp. |
| **EscalationLog** | Runtime | Record of an escalation and the substitute it produced. |
| **BroadcastMessage** | Runtime | A manager's message to all participants of an activation. |

> **Event feed note.** The dashboard's chronological event log is **synthesized** from the notification, call, response, escalation, task-completion, and broadcast tables — **no separate event table is needed**.

---

## 12. API Requirements

All endpoints are versioned under **`/api/v1/`** and secured by JWT. List endpoints are **filtered by the requester's role**.

| Endpoint | Purpose |
|---|---|
| `POST /auth/login` · `/auth/refresh` | Obtain and refresh tokens. |
| CRUD: organizations, departments, users, plans, teams, team-members, shift-assignments, task-templates | Core data management. |
| `POST /plans/{id}/activate` | Create an activation snapshot. |
| `GET /activations/{id}/dashboard` | Single aggregated monitoring payload. |
| `POST /activations/{id}/acknowledge` | Record a member's readiness. |
| `POST /activations/{id}/run-escalation` | Run one escalation cycle. |
| `PATCH /execution-tasks/{id}` | Mark done, add note, or reassign. |
| `POST /activations/{id}/broadcast` | Message all participants. |
| `POST /activations/{id}/set-substitute` | Set/change a participant's substitute live. |
| `POST /activations/{id}/raise-issue` | Leader reports an issue to the manager. |
| `GET /activations/{id}/my-tasks` · `/my-notifications` | A member's own tasks and inbox. |
| `POST /activations/{id}/close` | Close the activation. |

Execution tasks support **list and PATCH only** — marking done, adding a note, and (for leaders/managers) reassigning. Reassignment carries **object-level checks**: a leader can move tasks only between participants of teams they lead (403 across team boundaries), and moving a task to a participant of a different activation is rejected at validation.

---

## 13. Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-1 | **Real-time freshness.** Dashboards and member views shall update in **real time via SignalR** (WebSockets, with automatic transport negotiation). A state change — readiness tap, task completion, escalation, broadcast — shall propagate to all subscribed clients within ~1 second. Clients shall reconnect automatically and fall back to periodic REST polling of the aggregated snapshot if a persistent connection cannot be established (resilience on constrained municipal/emergency networks). |
| NFR-2 | **Operational simplicity.** The system shall run on a single internet-connected server. Real-time is served by an **in-process SignalR hub** within the ASP.NET Core host — no separate message broker, Celery-style worker, or background-job infrastructure is required at single-instance scale. (A Redis/SQL Server backplane is needed only if the hub is later scaled across multiple instances — see Section 13.1.) |
| NFR-3 | **Localization.** Arabic-first RTL; all user-facing strings live in translation files with a complete English locale. |
| NFR-4 | **Timezone correctness.** All shift logic and timestamps use Asia/Kuwait consistently across server, web, and mobile. |
| NFR-5 | **Usability.** Field screens present one primary action, large touch targets, and minimal text suitable for non-technical users. |
| NFR-6 | **Offline-tolerant assets.** Fonts and assets are bundled locally so the UI renders on networks without external CDNs. |
| NFR-7 | **Extensibility.** Notification delivery sits behind a provider interface so external channels integrate without changing activation, escalation, or broadcast logic. |
| NFR-8 | **Data integrity.** Activation, escalation, and broadcast execute atomically; a running activation is immutable to plan edits. |
| NFR-9 | **Portability.** Local development runs with a single database container, with a low/zero-dependency database mode for evaluation. |

### 13.1 Real-time transport & scale-out

Real-time delivery is provided by **ASP.NET Core SignalR**, hosted **in-process** within the API/web host (a `DashboardHub`). Design notes:

- **Push model.** On a relevant state change (readiness acknowledgment, task done/undo, escalation, substitute induction, broadcast, closure), the server recomputes the affected slice of the aggregated payload and pushes it to clients subscribed to that activation's hub group. On connect, a client receives the current full snapshot.
- **Transport negotiation.** SignalR negotiates the best available transport — **WebSockets** first, degrading automatically to Server-Sent Events or long polling where intermediaries block sockets. This preserves resilience on closed municipal/emergency networks without bespoke code.
- **Resilience fallback.** Clients use automatic reconnect; if a persistent connection cannot be established at all, the client falls back to **periodic REST polling** of the same aggregated snapshot endpoint (the original 10-second behavior remains available as a safety net, not the primary path).
- **Single-instance default.** One server hosts API, Razor web, and the hub — no broker required. **Scale-out** to multiple instances requires a backplane (**Redis** or the **SQL Server** backplane); this is deferred (Section 7.2) and changes deployment only, not application logic.
- **Mobile.** The Flutter app subscribes to the hub via a SignalR client package (e.g., `signalr_netcore`), with the same reconnect-and-fallback behavior.
- **Auth.** Hub connections are authenticated with the same JWT as the REST API; group membership is scoped by role so a client only receives activations it is permitted to see.

---

## 14. Security & Permissions Matrix

Authorization is enforced at **two layers**: per-endpoint role gates and per-record queryset filtering. A check mark means the action is permitted; data is additionally narrowed to the user's own scope where noted.

| Capability | Admin | Manager | Leader | Member |
|---|---|---|---|---|
| Manage orgs / departments / users | ✓ | read-only | – | – |
| Create / edit plans | ✓ | ✓ | – | – |
| Activate a plan | ✓ | if authorized | – | – |
| View live dashboard | ✓ | ✓ | own teams | – |
| Run escalation / broadcast / close | ✓ | ✓ | – | – |
| Acknowledge readiness | – | – | ✓ | ✓ |
| Complete tasks / add notes | ✓ | ✓ | own team | own tasks |
| Reassign tasks | ✓ | ✓ | own team only | – |
| Set substitute (live) | ✓ | ✓ | own team | – |
| Raise issue to manager | – | – | ✓ | – |

---

## 15. Internationalization & Localization

- **Arabic-first.** Both applications default to Arabic with full RTL layout and field-staff-appropriate labels.
- **Complete English locale.** A full English translation ships alongside; a language toggle flips direction on web and switches instantly on mobile.
- **Single source of strings.** All user-facing text lives in one translation resource per app; adding a language is one additional object with the same keys.
- **Regional conventions.** Kuwait timezone and the three fixed shift bands are built into the domain logic, not configured per install.

---

## 16. UI / Screen Inventory

### Web application (ASP.NET Core MVC/Razor, Arabic RTL)
| Screen | Role | Purpose |
|---|---|---|
| Login | All | Single-action sign-in. |
| Users administration | Admin | User table + add-user form. |
| Departments administration | Admin | Department table + add form. |
| My Plans (خططي) | Manager | Plan list with status badges. |
| Create-Plan Wizard | Manager | 4 steps: plan info · teams & members · tasks · shifts & review. |
| Plan Detail | Manager | Oversized **«إطلاق الخطة»** button with confirmation. |
| Live Dashboard | Manager / Leader | Counters, rates, team ranking, overdue, event log, escalation & broadcast actions, close. |
| Activation Summary | Manager | Final counts after closure (no live updates). |

### Mobile application (Flutter, Arabic RTL)
| Screen | Role | Purpose |
|---|---|---|
| Login | All | Sign-in. |
| Member Home | Member | Active-plan card, **«أنا جاهز ✅»**, task checklist with notes, broadcast inbox. |
| Leader Home | Leader | Team progress, task reassignment, substitute management, raise-issue. |
| Manager Home | Manager | Mini live dashboard, escalation, broadcast, close, plans with per-plan activate. |

### Design language (carried forward)
Operations-room **navy** for chrome; **amber** reserved for consequential actions (plan launch); **green** for acknowledgment/completion; **red** for escalation/overdue; **one oversized action per screen**. Typeface **IBM Plex Sans Arabic**, bundled locally so the UI renders on networks with no font-CDN access.

---

## 17. Technical Architecture & Platform Decisions (Re-Platform)

> This section reflects **decisions for the new build** and supersedes the original Django/Next.js/Expo implementation, while preserving its proven architecture (service layer, provider seam, snapshot model). Real-time delivery is upgraded from polling to SignalR push (Section 13.1).

### 17.1 Target stack
| Concern | Original MVP | Re-platform target |
|---|---|---|
| Backend API | Django 5 + DRF | **ASP.NET Core Web API (.NET 10)** |
| Web app | Next.js 14 (RTL SPA) | **ASP.NET Core MVC / Razor** (server-rendered, Arabic RTL) |
| Mobile app | Expo / React Native | **Flutter** (RTL native, secure token storage, SignalR real-time client) |
| Database | PostgreSQL | **SQL Server** (EF Core) |
| ORM | Django ORM | **Entity Framework Core** |
| Auth | `simplejwt` access+refresh | **ASP.NET Core JWT bearer + refresh-token rotation** |
| Domain logic | Service layer (shared by API/CLI) | **`Application` project** — services called by API and CLI |
| Escalation trigger | `manage.py run_escalation` | **CLI command** invoking the same `EscalationService` |
| Notifications | `NotificationProvider` + DB placeholder | **`INotificationProvider`** + `DatabasePlaceholderProvider` + DI |
| Real-time | 10-second polling | **SignalR real-time push** (WebSockets + auto-fallback; REST polling as resilience fallback) |
| Timezone | Asia/Kuwait at settings level | Store UTC; **resolve shifts against Asia/Kuwait explicitly** |

### 17.2 Solution layout (clean architecture)
```
ExecPlan.sln
  src/ExecPlan.Domain          entities, enums, base types
  src/ExecPlan.Application      ActivationService, EscalationService, DashboardService,
                                INotificationProvider, shift logic, DTOs
  src/ExecPlan.Infrastructure   EF Core DbContext (SQL Server), JWT/auth,
                                DatabasePlaceholderProvider, seeding
  src/ExecPlan.Api              ASP.NET Core MVC/Razor + Web API (hosts web UI)
  src/ExecPlan.Cli              run-escalation entrypoint
  mobile/                       Flutter app (member / leader / manager flows)
```

### 17.3 Architectural invariants (must be preserved)
- **One service layer** holds all domain logic so the API, the CLI, and any future scheduler trigger **identical** behavior.
- **Atomicity** (NFR-8): activation, escalation, and broadcast complete in a single transaction; the notification provider stages rows and the calling service performs one save.
- **Provider seam** (NFR-7): integrating Twilio voice / Infobip SMS / WhatsApp Business later means one new class and one DI line — no changes to activation, escalation, or broadcast.
- **Snapshot immutability** (FR-PLAN-8 / FR-ACT-1): a running activation never depends on the plan's mutable structure.
- **Single aggregated dashboard payload** (FR-MON-1): all aggregation server-side; delivered over the SignalR hub on state change (and as a snapshot on connect), with a REST endpoint returning the same payload for initial load and fallback.

### 17.4 Deployment topology
A single internet-connected VPS or cloud instance: the ASP.NET Core host (Web API + Razor web + in-process SignalR hub) behind a reverse proxy, SQL Server local or managed, and the Flutter app distributed to pilots pointed at the server's public URL. The **reverse proxy must permit WebSocket upgrade** (e.g., the `Upgrade`/`Connection` headers and a long proxy-read timeout) so SignalR negotiates WebSockets; where that is blocked, SignalR degrades automatically. Escalation may be driven by a scheduled task invoking the CLI until a built-in scheduler is introduced. Secrets and connection strings are environment-driven.

---

## 18. Release Plan & Roadmap to Revenue

### 18.1 Build phasing (re-platform)
1. **Backend spine** — solution, 15 entities, EF Core + SQL Server, JWT auth, activation/escalation/dashboard services, CLI.
2. **Web (MVC/Razor)** — login, admin tables, plan wizard, plan detail/activate, live dashboard, closure summary; Arabic RTL + English toggle.
3. **Flutter mobile** — role-routed home screens (member/leader/manager), readiness tap, task checklist, broadcast inbox, mini dashboard.
4. **Polish** — broadcast-to-inbox, close-to-summary, language toggle, physical-device RTL pass.

### 18.2 Roadmap to revenue (business)
Deploy a demo tenant and digitize two showcase plans → sign 2–3 paid pilots ahead of storm season (storm-response plan as the ideal first case) → integrate **one real notification channel** (WhatsApp Business or a local SMS gateway) → convert pilots to annual contracts and capture drill metrics as case studies → pursue the first government tender with an SI partner carrying the on-premises offering. Technical roadmap items map one-to-one onto what paying customers ask for first.

### 18.3 Pricing architecture (indicative; set against pilots)
| Tier | Intended buyer | Composition |
|---|---|---|
| **Team** | Single department or site | Flat monthly fee (~50 users), cloud-hosted, in-app notifications only. |
| **Operations** | Multi-team organizations | Per-active-user pricing; SMS/voice/WhatsApp metered with margin; priority support. |
| **Government / Enterprise** | Ministries, municipalities, oil sector | Annual on-premises license + maintenance (≈18–22%/yr), deployment, training, SLA. |

---

## 19. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Long government procurement cycles (6–18 months) | Lead with smaller private facilities-management deals for cash flow while tenders mature. |
| Dependence on a **readiness behavior change** among field staff | Deliberate one-button design; sell drills as part of onboarding. |
| Telecom integration cost/regulation (esp. voice in Kuwait) | Licensed local gateway partner; provider-interface architecture isolates the dependency. |
| Global platforms adding Arabic | Moat is **regional depth** (shift conventions, on-prem government deployments, local support), not the language toggle alone. |
| Single-market concentration in Kuwait | Open a second GCC market within ~18 months. |
| **Re-platform regression risk** (new build) | Preserve architectural invariants (17.3); verify each phase against the same acceptance criteria as the original MVP (Section 21). |
| Physical-device RTL layout edge cases | Dedicated device polish pass on first Flutter runs. |

---

## 20. Assumptions, Constraints & Dependencies

- **Assumptions:** customers already own written plans and shift rosters; pilots will demand at least one real notification channel immediately; market sizing (150–300 KW orgs) is a planning assumption to validate in pilots.
- **Constraints:** single-server operational footprint (in-process SignalR hub; no separate broker at single-instance scale); reverse proxy must allow WebSocket upgrade; Arabic-first RTL is mandatory; Asia/Kuwait shift model is fixed (not per-install configurable).
- **Dependencies:** SQL Server instance; reverse proxy; (future) licensed telecom gateway; SI partners for government go-to-market.

---

## 21. Acceptance Criteria & Verification

Each phase is accepted against concrete criteria, verified on the **running system** rather than by inspection.

| Phase | Acceptance criterion |
|---|---|
| **Backend** | Logging in as each role, activating the seeded plan, acknowledging as a member, running escalation, and retrieving the dashboard return correct numbers; member-only visibility holds; the threshold number of attempts induct the substitute. |
| **Web** | A non-technical manager can create and activate a plan and watch the live dashboard, entirely in Arabic; production build is clean. |
| **Mobile** | A member acknowledges and completes tasks from the phone and the web dashboard reflects it in real time (within ~1 second via SignalR; via the polling fallback if the socket is unavailable). |
| **Polish** | Broadcast reaches the member inbox in real time; closing an activation produces the summary and stops live updates; the SignalR connection recovers across a brief network drop (auto-reconnect, then polling fallback); the language toggle works on both apps. |

**Residual verification note (carried from MVP):** physical-device layout (spacing and RTL edge cases on real phones) warrants a dedicated polish pass on first device runs.

---

## 22. Open Questions & Decisions Log

| # | Item | Status |
|---|---|---|
| D-1 | Web framework | **Decided:** ASP.NET Core MVC/Razor (over Blazor). |
| D-2 | Database | **Decided:** SQL Server (switched from PostgreSQL). |
| D-3 | Backend platform | **Decided:** .NET 10 (LTS). Mobile: Flutter (current stable). |
| D-4 | Real-time transport | **Decided:** **SignalR real-time push** (WebSockets + auto-fallback), replacing the original 10-second polling. REST polling retained only as a resilience fallback. |
| Q-1 | First real notification channel (WhatsApp Business vs. local SMS gateway) | Open — driven by first pilot's demand. |
| Q-2 | On-premises packaging/installer for government tier | Open — design during pilot-to-tender phase. |
| Q-3 | Scheduler technology for automatic timed escalation | Open — deferred; CLI seam already in place. |
| Q-4 | Indicative price levels per tier | Open — to be set against pilot feedback. |
| Q-5 | Hub scale-out backplane (Redis vs. SQL Server) **if** multi-instance is needed | Open — not required at single-instance MVP scale; revisit at scale. |

---

## 23. Glossary

| Term | Meaning |
|---|---|
| **Plan** | A reusable template: teams, task templates, roster, metadata. |
| **Activation** | An immutable snapshot created when a plan is launched. |
| **Participant** | A person frozen into an activation from the roster. |
| **Response** | The single readiness tap «أنا جاهز» — the only counted acknowledgment. |
| **Escalation** | Calling non-responders and inducting substitutes after the attempt threshold. |
| **Substitute** | A pre-designated backup, resolved from the roster and inducted on escalation. |
| **Shift** | One of three fixed daily bands in Asia/Kuwait. |
| **Broadcast** | A manager message delivered to all participants of an activation. |

---

*EXECPLAN — Product Requirements Document · v1.1 (re-platform baseline) · June 2026 · Consolidated from the Business Model Report, Features & Requirements Specification, and Technical Report (all MVP 1.0). Rev 1.1: real-time transport = SignalR/WebSockets.*
