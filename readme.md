# Retirement Stress Test — Specification & README (V5.0.1)

A complete, authoritative, deterministic description of the simulation engine and its business rules.  
There is **no logic in the program that is not documented here**, and **no rule documented here that is not implemented in the program**.

If any ambiguity arises, the section **Authoritative Year Execution Order** takes precedence.

---

# 📌 Overview

The **Retirement Stress Test** is a deterministic C# simulation engine that models the behaviour of **two linked SIPPs (S1 and S2)** over a 40‑year retirement horizon.

The model evaluates:

How different simulation strategies (engines) perform given the same market conditions, spending plan and time horizon.  


The engine answers one question:

    “Given a planned retirement spending path, how do two SIPPs behave under real market returns for the three different strategies we test?  Ultimatley answering, which strategy *may* perform the best when backtested against all our available data.”.  Returns and spending plans are provided via input fles, enabling simulations to run for different spending plans and market conditions.

---

## Spending plan

### Objectives

The spending plan is built around **three objectives**, each with a different priority and flexibility:

- **O1 – Essential / “Base” spending (the vase)**  
  Core, non‑negotiable spending required to maintain basic lifestyle.  
  In downturns, the strategies allow **only minor cuts (up to 10%)** to O1.

- **O2 – Lifestyle / “GoGo” spending**  
  Higher‑priority discretionary spending that enhances lifestyle.  
  In downturns, O2 is reduced **after O3**, but **before** touching O1.

- **O3 – Gifting / Legacy spending**  
  Lowest‑priority, fully flexible spending (e.g., gifts, support, optional extras).  
  In downturns, **O3 is cut first**, often fully, to protect O1 and O2.

Across all strategies (Momentum, Hybrid, Bucket), the hierarchy is:

**O3 cuts first → then O2 → O1 only trimmed by up to 10% in severe conditions.**

---

### Front loading

The simulation supports **two independent SIPP accounts (S1 and S2)**, each with its own annual planned withdrawals thoughout the plan.  These are loaded from the input CSV files.

Early testing used **front‑loaded spending** for O1, O2, and O3 to stress‑test the engines.  
The CSV files allow you to model:

- **External income, e.g. State Pension income or savings**  
- **Changing spending patterns over time**  
- **Different SIPP responsibilities for each objective**

This makes the spending plan fully configurable and realistic.

---

### Returns

Market returns are read from the **market‑returns CSV file**, which contains:

- **Annual real returns** in all cases (after inflation see https://iamkate.com/data/uk-inflation/)
- **Global Equity Tracker** data was obtained from here https://curvo.eu/backtest/en/compare-indexes/msci-world-vs-msci-usa?currency=gbp https://curvo.eu/backtest/en/market-index/msci-world?currency=gbp 
- **Global Bonds**, as described in the next section, is a synthetic series constructed using a regime‑based model to approximate real bond returns for a global short/intermediate duration allocation in GBP terms.
- **Global 6040ETF** , as described in the next section, is a synthetic series representing a 60/40 global equity/bond portfolio in real GBP terms.
- Covering the period **1966–2025**

This allows simulations to start in any year within that range and run for up to 40 years.

All return calculations inside the engines use **real (inflation‑adjusted) returns**, ensuring that spending, guardrails, bonuses applied, impact on buckets operate in real terms.

#### Global Bond returns
The bond series (GlobalShortAndIntBondETF_RealReturns) is a synthetic UK-investor proxy for a global short/intermediate duration bond allocation in GBP terms. It is constructed using a regime-based model rather than a single historical ETF, combining interest rate behaviour and inflation dynamics to approximate real bond returns consistently over time. Returns are expressed in real terms using UK CPI inflation.

For the period 1966–1989, the series is primarily anchored to UK government gilt yield behaviour, using historical UK inflation data (Office for National Statistics / long-run CPI reconstruction) and known gilt market regimes as the reference structure. This captures the high-inflation 1970s environment (weak or negative real bond returns) and the subsequent early-1980s disinflation period, where falling yields drove strong real bond performance. Direct global bond index data is not reliably available for this period, so UK gilt behaviour is used as the closest investable proxy.

From 1990 onwards, the series is aligned more closely with global aggregate bond market behaviour, broadly consistent with Bloomberg Global Aggregate Bond Index characteristics in GBP terms (and comparable global intermediate-duration indices). This period reflects structurally lower inflation, declining yields through the 1990s–2010s bond bull market, and a post-2020 regime shift driven by inflation spikes and rapid interest rate adjustments. UK CPI inflation is applied throughout using the provided historical series to ensure consistent real-return conversion across the full dataset.

#### Global 6040ETF
The GlobalETF6040_RealReturn series represents a synthetic 60/40 global equity/bond portfolio in real (inflation-adjusted) GBP terms. It is constructed using a weighted combination of 60% MSCI World GBP total returns and 40% synthetic global short/intermediate bond returns. The underlying equity and bond series are first expressed in real terms using UK CPI inflation, ensuring consistent purchasing-power-based returns across asset classes.

Portfolio returns are calculated using a weighted annual return approximation, combining equity and bond components in proportion to their allocation weights. This provides a close approximation to geometric portfolio performance in low single-digit return regimes and maintains consistency with long-run backtesting conventions used in retirement modelling.

## Simulation strategy overview

This simulator supports **three distinct withdrawal and cash‑management strategies**, each representing a different philosophy for handling sequence‑of‑returns risk, cash buffers, and portfolio maintenance.

As a general note.  all engines use the concept of guard-rails to reduce spending in bad conditions, and the concept of a bonus to provide additional withdrawals when in good conditions.  Whilst the triggers will vary slightly between engines, the amounts do not.  Common guard-rails and bonus logic is intentional, to ensure how they work does not distort the performance of the engines (i.e. we dont want differing engine results based on how their guardrails and bonuses work).

---

### 1. Momentum rebalance (Strategy 1)

**Overview**

A single‑portfolio, total‑return approach using a blended 60/40 ETF (global equity/global short&intermediate bonds)
This strategy maintains a **multi‑year cash buffer**.  The cash buffer only refills it when market conditions are favourable, and flexes the number of "years of spend" to move to cash based on health (more health, more to cash buffer).
Withdrawals: in poor conditions (when previous year returns are negative, or when the SIPP  is below a "health threshold" - (health determines if the SIPP has sufficient value for its discounted future liabilities)) withdrawals come from the cash-buffer (to prevent selling equities in poor conditions), in all other cases, withdrawals come from the ETF.
Guardrails reduce spend in challenging conditions, bonuses increase spend in good conditions.

**Philosophy**

- **Keep things super-simple**  
  Nothing complex to manage, a simple "self-balancing" ETF and a cash buffer.

- **Do not sell ETF after a bad year**  
  Refills are skipped when the previous year’s market return is negative.

- **Let the ETF recover before drawing from it**  
  Cash buffer absorbs withdrawals during downturns.

- **Cash buffer may shrink during downturns**  
  This is intentional — it avoids forced selling.

- **Refill Cash buffer aggressively during recoveries**  
  When markets rebound, the strategy tops up cash back to the target number of years.

This is a classic **guardrails + cash buffer** approach with momentum‑aware refill timing.

---

### 2. Dynamic time buckets (Strategy 2)

**Overview**

A **time‑segmented, state‑aware bucket strategy** that divides each SIPP into three buckets:

- **B1 – Short‑term cash bucket** (next few years of spending)  
- **B2 – Medium‑term defensive bucket** (the following several years)  
- **B3 – Long‑term growth bucket** (everything beyond the B1+B2 horizon)

Each year, the engine applies guardrails, determimes bonus processes withdraws and then refills.
 Withdrawals always come from **B1** (but if it has insufficient funds it will come from → B2 → B3)
 Refills are **state‑aware**.  In normal or good conditions, buckets are fully filled to contain the required value for their spend horizon (B3 has what is left).  In challenging conditions, a cap of either 1 or 2 years required spend is put on any refills (avoiding selling assets when down)
 Guardrails reduce spend in challenging conditions, bonuses increase spend in good conditions.

**Philosophy**

- **Have a long spending runway to try to avoid equity sales in prolonged downturns**  
  B1 and B2 are sized directly from the future spending curve, giving a clear multi‑year buffer.

- **Withdraw in strict time order**  
  B1 funds near‑term spending, B2 supports the medium term, and B3 is preserved for long‑term growth.

- **Refill intelligently based on funding state**  
  Refill is attempted every year using this process...B2 → B1 and B3 → B2.  But the *behaviour* adapts to conditions resulting in differing amounts to refill:  
  - **Normal / Good** → full refills 
  - **Bad** → capped refills 
  - **ExtremeBad** heavily capped refills 
  
This creates a **state‑aware, return‑aware, time‑segmented system** that maintains runway stability while avoiding forced selling of the weakest assets.

---

### 3. Hybrid

**Overview**

A three‑bucket system combining the strengths of Momentum and the Time Bucket approaches (conceptually most similar to momentum) :

- **B1** = time‑based cash bucket (3 years)  
- **B2** = 40% defensive assets (global bonds) 
- **B3** = 60% growth assets (global equities)

Withdrawals are always from cash (and if cash is not sufficient, remainder is pro-rata from B2 and B3).

The Hybrid engine uses **Momentum‑aligned refill logic** (same timing and amount as Momentum),  
but differs in **what it sells** to fund refills.  In Hybrid, the "better performing asset" is sold to fund the refill.

**Smart Rebalancing** - After withdrawals and the refill of B1, an attempt is made to rebalance the B2 and B3 to their target allocations.  A tollerance (e.g. 5% drift) is allowed between current allocation and target (e.g. for 60/40 a 55 tollerance allows between 35/65 to 45/55).  If rebalance is required (drift is outside of tollerance) then the health of the bucket than needs reducing is determined.  In good health, we rebalance to target (e.g. 60/40) if bad health conditions, we re-balance to closest "in tollerance" position (e.g. to 35/65 or 45/55) to avoid selling more than needed when down.

**Philosophy**

- **Withdraw from B1 first always**  
  B1 is for withdrawals, we only dip to B2 / B3 (pro-rata contributions) if insufficient in B1 to meet withdrawal need.

- **Use Momentum logic to decide *when* and *how much* to refill cash**  
  Hybrid only refills when Momentum would — but crucially refill funded from best performing asset.

- **Rebalance cautiously**  
  Only when drift exceeds tolerance *and* the asset being sold is not down.

This approach is inspired by the bucket‑based sequence‑risk research from  
Michael Kitces and the framework described in:  
https://www.youtube.com/watch?v=3ASQi1IUHws  
and  
https://www.kitces.com/blog/managing-sequence-of-return-risk-with-bucket-strategies-vs-a-total-return-rebalancing-approach/

The Hybrid strategy aims to combine:

- Momentum’s **timing discipline**  
- Bucket strategies’ **selective selling**  
- A more **adaptive, health‑aware** withdrawal process

while avoiding the pitfalls of forced refills and rigid time‑bucket rules.

### What all strategies have in common

All strategies:

- Use the same guardrails reduction values
- Use the same bonus increase values
- Apply market returns at year‑end  
- Maintain the same objective accounting and reconciliation rules  (e.g. track O1Plan vs O1Actual, record "Bonus" payments in up years)
- Produce a ** Simulation Result ** which allows simulations run with different engines to be compared objectively.

---

# 🔄 Authoritative year execution order (deterministic specification)

## Overview

This section defines the **exact**, **strategy‑agnostic** order of operations for every simulation year.  
All engines (Momentum, Time Buckets, Hybrid) follow this sequence **without exception**.

If any other section appears to conflict with this one, **this section takes precedence**.

Each simulation year is composed of a fixed set of steps.  
Some steps occur at the **start of the year (1st January)** and others at the **end of the year (31st December)**.  
There are **no mid‑year operations**.

The detailed behaviour of each step is described in later sections, but the list below provides the high‑level annual structure.

---

### **🗓️ 1st January — Start‑of‑Year Steps**

- **Calculate SIPP health**  
  (baseline, funding ratio, funding state, bucket health, safe runway)

- **Calculate external funding**  
  (portion of O1/O2/O3 covered outside the SIPPs)

- **Calculate per‑SIPP contributions to planned objectives**  
  (split O1/O2/O3 between S1 and S2)

- **Apply Guard Rails**  
  (O3 → O2 → O1 cuts based on funding state and bucket health)

- **Apply Bonus**  
  (if funding ratio exceeds thresholds)

- **Calculate withdrawals for each SIPP**  
  (bucket‑order withdrawals + optional S1→S2 rescue)

- **Refill / Rebalance**  
  (strategy‑specific behaviour, but always executed at this point)

---

### **🗓️ 31st December — End‑of‑Year Step**

- **Apply that year’s market returns**  
  (e.g., the 1973 return is applied on the last day of 1973)

Returns update B2 (and also B3 for time buckets and hybrid) with the approipriate market returns.  B1 (cash) never has returns applied, it is assumed it just keeps up with inflation.

---

## Simulation setup...  Year 1 cash / bucket seeding (before Step 0)

Before the first simulation year begins:

- Each SIPP’s opening balance (from the config file) is split into the appropriate buckets using the bucket‑target rules defined within the strategy.  Momenutum uses **B1**, **B2**, whilst Time Buckets and hybrid uses **B1**, **B2** and **B3**.  
- This creates the **initial bucket state** for Year 1.  
- This seeded state **is included** in the start‑of‑year pot for Year 1.  
- All health metrics (funding ratio, bucket health, safe runway) for Year 1 are computed **after** this seeding.

This ensures that Year 1 begins with a **fully initialised bucket structure**, consistent across all strategies.

---

## Step 0 — Start‑of‑year state

At the start of each year, each SIPP has a current balance for each bucket in that strategy (for each sipp).  The balance is generally the closing balance after the final step of the previous year (apply returns), apart from at the start of the simulation, where they are set based on the SIPP opening balances (see previous step)

At this stage, no steps for the current year have occurred.  This represents "start of year state" is used for the health calculation.

---

## Step 1 — Calculate health (once per year)

Health is calculated **once**, at the start of the year, and **reused unchanged** for all decisions in that year.

    CurrentPot = sum (all buckets)
    RemainingLiabilities = PV of all future CSV S1Spend/S2Spend values (including current year)
    Health = CurrentPot / RemainingLiabilities

- Discount rate = 1% real  
- Liabilities include the current year (e.g. if current year is 1973, at this point we have not paid 1973 withdrawals, so 1973 planned payments are included in remaining liabilities)
- Health is **not recalculated** after withdrawals, bonus, rescue, refill, or returns  

---

## Step 2 — Apply external funding waterfall

External funding is calculated for the current year.  External funding is defined as the difference between the planned O1 to O3 spend, and the planned S1Spend and S2Spend.  External Funding is the portion of "planned spending" that is funded externally to the SIPPs (state pension, other investments, anothr pension for example).  The simulation assumes 100% of external funding occurs every year (without fail).

External funding is applied in the following order:

1. O1 (Essential)  
2. O2 (Lifestyle)  
3. O3 (Luxury/Gifting)  

This determines the **SIPP‑funded portion** of each objective:

    SippFunded_Oi = Oi_Planned − ExternalFunded_Oi

---

## Step 3 — Allocate SIPP responsibility (proportional funding)

Each SIPP funds its proportional share of the SIPP‑funded O1/O2/O3 amounts:

    TotalSippSpend = S1Spend + S2Spend
    S1Ratio = S1Spend / TotalSippSpend
    S2Ratio = S2Spend / TotalSippSpend

Each SIPP’s O1/O2/O3 responsibility is then:

    S1_Oi = SippFunded_Oi × S1Ratio
    S2_Oi = SippFunded_Oi × S2Ratio

This proportional allocation is used for:

- Guardrails  
- Debt tracking  
- Withdrawal classification  
- Objective fulfilment  
- Rescue behaviour  

---

## Step 4 — Apply guardrails

Guardrails reduce each SIPP’s proportional O1/O2/O3 responsibility based on that SIPP’s **start‑of‑year health** (before withdrawals, before refills, before returns).

The guardrail tiers are identical across all engines:

| Tier | Action |
|------|--------|
| **1** | Cut **50%** of O3 |
| **2** | Cut **100%** of O3 |
| **3** | Cut **100%** of O3 **+ 75%** of O2 |
| **4** | Cut **100%** of O3 **+ 100%** of O2 **+ 10%** of O1 |

Guardrails always operate on **each SIPP’s proportional share** of the SIPP‑funded portion of each objective.

Guardrails are **not applied in Year 1**.  
They begin in Year 2, once the plan has started operating and health can meaningfully deteriorate.

### Early‑cuts tracking (first 10 years)

All engines track the amount of **O2 and O3 cuts applied during the first 10 years** of the simulation.  
These values do not affect behaviour; they are recorded purely for reporting and analysis.

For each SIPP:

- EarlyO2Cuts += (O2 cuts applied this year)
- EarlyO3Cuts += (O3 cuts applied this year)

This tracking occurs **only for years 1–10** (0‑indexed years 0–9 in code).  
The totals are returned in the final `SimulationResult` as:

- `EarlyO2Cuts`
- `EarlyO3Cuts`

This allows the model to quantify how “harsh” the early‑retirement period was under each strategy.

---

### Tier triggers by strategy

Each strategy uses a different mechanism to determine which tier applies.  
The *actions* are identical, but the *triggers* differ.

| Tier | Momentum Trigger | Time Bucket Trigger | Hybrid Trigger |
|------|------------------|----------------------|----------------|
| **1** | Funding Ratio (FR) < **0.80** | **FinalTier = 1** when:<br>• SafeRunway ≥ 7 **and** B1Health ≥ 0.50<br>• FRTier = 1 | **FR‑based guardrails** (same as Momentum):<br>FR < **0.80** |
| **2** | FR < **0.75** | **FinalTier = 2** when:<br>• SafeRunway ≥ 5 **and** B1Health ≥ 0.30<br>• FRTier = 2 | FR < **0.75** |
| **3** | FR < **0.70** | **FinalTier = 3** when:<br>• SafeRunway ≥ 3<br>• (B1Health may be low)<br>• FRTier = 3 | FR < **0.70** |
| **4** | FR < **0.65** | **FinalTier = 4** when:<br>• SafeRunway < 3 **or** B1Health extremely low<br>• FRTier = 4 | FR < **0.65** |

#### Notes on the triggers

- **Momentum**  
  Uses *only* the **funding ratio** (pot ÷ discounted liabilities).  
  Tier = 0–4 based purely on FR thresholds.

- **Time Buckets**  
  Uses a **two‑axis system**:  
  - **BHTier** from *bucket health* (B1Health + SafeRunway)  
  - **FRTier** from funding ratio  
  FinalTier = **max(BHTier, FRTier)**  
  → This makes Time Buckets the most sensitive to early‑runway deterioration.

- **Hybrid**  
  Uses **Momentum’s guardrail logic exactly** (FR‑based tiers).  
  Hybrid differs from Momentum only in *withdrawal routing* and *refill source*, not in guardrail triggers.

---

### CashShield

CashShield is a **Momentum‑style protection rule** that prevents unnecessary guardrail cuts when the SIPP has:

- **Enough cash to cover the year’s planned SIPP spend**, **and**
- **The previous year’s market return was negative**, **and**
- **Health is below the CashBufferHealthThreshold**

When CashShield activates:

- Guardrails are **suppressed for that year** (Tier forced to 0)
- The SIPP spends from cash without cutting O1/O2/O3
- No refill is allowed (to avoid selling ETF in a down year)

#### Engines using CashShield

| Strategy | Uses CashShield? | Notes |
|----------|------------------|-------|
| **Momentum** | **Yes** | Original CashShield design |
| **Hybrid** | **Yes** | Uses Momentum’s CashShield logic exactly |
| **Time Buckets** | **No** | Always refills B1/B2 based on runway; no CashShield concept |

CashShield is one of the key behavioural differences between Momentum/Hybrid and Time Buckets.

---

## Step 5 — Determine base withdrawal target

After guardrails have been applied (if they are applied), each SIPP now has a reduced set of  
**SIPP‑funded O1/O2/O3 responsibilities** for the year.

For each SIPP:
- BaseTarget = (Post‑guardrail O1) + (Post‑guardrail O2) + (Post‑guardrail O3)


This BaseTarget is the **withdrawal requirement before bonus is added**.

- Guardrails directly modify the O1/O2/O3 portions.
- The BaseTarget is simply the sum of those post‑cut values.
- Bonus (Step 6) increases this target (if conditions are met).
- Withdrawals (Step 7) attempt to meet the final target.

The BaseTarget is therefore not a separate algorithm, but a **defined output** of the guardrail step and the **input** to bonus and withdrawals.

*note:* It is not possible for a single SIPP in a given year to have BOTH guard-rails and bonus - they are mutually exclusive.  Guardrails apply in "down conditions" (to reduce current withdrawals to protected long term success) and bonus applies in "up conditions" where surplus is available to boost withdrawals.

---

## Step 6 — Calculate bonus (after guardrails, before withdrawals)

Bonus is an **upward adjustment** to the withdrawal target, awarded only when a SIPP is in **strong health**.  
It is calculated **after guardrails** but **before withdrawals**.

A bonus is **never** paid when health is weak or marginal.

---

### When is a bonus considered?

A SIPP is eligible for bonus **only if its start‑of‑year health exceeds the bonus StartThreshold**:
- If Health ≤ StartThreshold → Bonus = 0
- If Health > StartThreshold → Bonus is evaluated


All three engines use the **same bonus logic** and the same thresholds.

---

### Bonus tiers (Momentum / Time Buckets / Hybrid — identical)

Bonus tiers mirror the guardrail structure (Tier 0–5), but **inverted**:  
higher health → higher tier → higher bonus rate.

| Tier | Health Range | Bonus % of Pot |
|------|--------------|----------------|
| **0** | Health ≤ StartThreshold | 0% |
| **1** | StartThreshold < H < Tier2Threshold | PctTier1 |
| **2** | Tier2Threshold ≤ H < Tier3Threshold | PctTier2 |
| **3** | Tier3Threshold ≤ H < Tier4Threshold | PctTier3 |
| **4** | Tier4Threshold ≤ H < Tier5Threshold | PctTier4 |
| **5** | H ≥ Tier5Threshold | PctTier5 |

These percentages come directly from `config.Bonus` and are identical across engines.

---

### Bonus formula

Once a SIPP qualifies for a bonus tier:
- Pot = B1 + B2 + B3   (or Cash + ETF in Momentum)
- AnnualSurplus = Pot − PV(RemainingLiabilities)
- BonusRaw = Pot × BonusRate
- Bonus = min(BonusRaw, max(AnnualSurplus, 0))


Key points:

- Bonus is **capped by surplus** — you cannot bonus yourself into deficit.
- Bonus is **never negative**.
- Bonus is **added to the withdrawal target**, not paid separately.

---

### Final withdrawal target

After guardrails (Step 4) and bonus (Step 6):
- WithdrawalTarget = BaseTarget + Bonus


This is the amount the withdrawal engine attempts to satisfy in Step 7.


In summary, all engines use **identical bonus thresholds and behaviour**.

---

## Step 7 — Execute withdrawals (strategy‑specific bucket sequencing + rescue)

Withdrawals represent money actually taken **out** of each SIPP in a given year.  
Internal movements (refills, rebalancing, bucket‑to‑bucket transfers, rescue transfers) are **not** withdrawals.

All strategies share the same high‑level structure:

1. Compute **WithdrawalTarget** (BaseTarget + Bonus)  
2. Attempt to withdraw that amount using the strategy’s bucket rules  
3. If S2 cannot meet its target, S1 may perform a **rescue withdrawal**  
4. Any remaining unmet amount becomes **objective under‑delivery**

However, **each strategy uses a different rule for which bucket pays first**.

### Depletion threshold (pot ≤ 1.0)

A SIPP is considered **depleted** when its total pot (sum of all buckets) falls to  
**£1.00 or below** at any point during the simulation year.

When depletion occurs:

- The engine records the **depletion year index** for that SIPP  
- All future withdrawals for that SIPP become **zero**  
- No further guardrails, bonus, refill, rescue, or rebalancing steps apply  
- The SIPP remains at zero for the rest of the simulation

This threshold is implemented consistently across all strategies.

---

## 🔹 Strategy 1 — Momentum (Cash + ETF)

Momentum uses a **two‑bucket** model:

- **Cash** (B1)
- **ETF** (B2)

Momentum’s withdrawal rule is **sequencing‑based**, depending on health and previous return:

### **Withdrawal sequencing**

- If (Health < CashBufferHealthThreshold OR PreviousReturn < 0)
- Use Cash first, then ETF
- Else
- Use ETF first, then Cash


This creates the classic Momentum behaviour:

- In bad years → **protect ETF**, spend cash  
- In good years → **protect cash**, spend ETF

### **Partial cash availability**

If the preferred bucket cannot fully cover the withdrawal:

- Use all of it  
- Then fall back to the other bucket

### **Negative balances**

Cash and ETF are **floored at zero**.

### **Rescue (S1 → S2)**

If S2 cannot meet its withdrawal target:

- S1 covers the shortfall  
- S1 uses **its own sequencing rule** (based on S1’s health and previous return)  
- Rescue counts as **S1 withdrawal**  
- Guardrails and bonus are **not recalculated**  
- If S1 cannot fully cover → the remainder becomes unmet

---

## 🔹 Strategy 2 — Time Buckets (B1 → B2 → B3)

Time Buckets uses a **strict time‑segmented withdrawal order**:

- Withdraw from B1 (short‑term) → then B2 (medium‑term) → then B3 (long‑term)


There is **no sequencing logic**, no health‑based switching, and no market‑based switching.

### **Withdrawal rules**

- Always withdraw from **B1 first**  
- If B1 is insufficient → withdraw from **B2**  
- If B2 is insufficient → withdraw from **B3**  
- No bucket is allowed to go negative

This reflects the philosophy of the strategy:

- B1 is the spending runway  
- B2 supports the next stage  
- B3 is long‑term growth

### **Rescue (S1 → S2)**

Time Buckets **does not implement rescue**.  
Each SIPP stands alone.  
If a SIPP cannot meet its target, the unmet portion becomes under‑delivery.

---

## 🔹 Strategy 3 — Hybrid (B1 then pro-rata B2 + B3 if B1 insufficient)

Hybrid has three buckets:

- **B1** — cash runway  
- **B2** — defensive assets (bonds)  
- **B3** — growth assets (equities)

### **Withdrawal routing logic**

- Withdraw awlays funded from B1
- If B1 is insufficient, empty B1 and → withdraw the unment from B2 / B3 in pro-rata fashion (based on the current allocations)
- No bucket is allowed to go negative

### **Rescue (S1 → S2)**

Hybrid **does** implement rescue:

- If S2 has unmet withdrawal  
- Rescue counts as **S1 withdrawal**  
- Guardrails and bonus are **not recalculated**

If S1 cannot fully cover → remainder becomes unmet.

---

## Summary — Withdrawal behaviour by strategy

| Strategy | Buckets | Withdrawal Order | Sequencing Logic | Rescue? |
|----------|----------|------------------|------------------|---------|
| **Momentum** | Cash + ETF | Cash→ETF *or* ETF→Cash | Yes — based on health + prev return | **Yes** |
| **Time Buckets** | B1, B2, B3 | B1 → B2 → B3 | None | **No** |
| **Hybrid** | B1, B2, B3 | B1 → (pro-rata B2/B3) | None | **Yes** |

---

## Step 8 — Refill (strategy‑dependent)

Refill happens **after withdrawals** but **before end‑of‑year returns**  

Refill is the mechanism that moves money **between buckets** to restore the
desired runway or allocation for the *next* year.

Refill **never** counts as a withdrawal.  
Buckets are never allowed to go negative — partial refill occurs if needed (as dictated by the strategy).

---

## 🔹 Overview — What “refill” means

Each strategy has a different philosophy:

- **Momentum**  
  Refill **cash buffer** only when markets are favourable, and with a cap on amount if very poor conditions (momentum‑aligned).

- **Time Buckets**  
  Refill **B1 and B2 every year**, but refills are capped in bad conditions (2 levels, bad cap and extremely bad cap).

- **Hybrid**  
  Refill **B1 only**, using **Momentum timing**, but choosing the **source bucket**  
  (B2 or B3) based on relative asset strength.  
  After refill, Hybrid performs an **extra step**: B2/B3 **rebalance**.

All strategies use **raw CSV S1Spend/S2Spend** (pre‑guardrail, pre‑bonus)  
to determine how many years of future spending should be held in B1 (and discounted future spending for B2 for Time Buckets).

---

## 🔹 Refill target (shared across strategies)

All engines have intelligene to determine how much they should refill each year, this is based upon the engines measure of health.
 
For Hybrid, the “health” used is the **overall SIPP health** (FR‑based).  
For Momentum, the same rule applies.  
Time Buckets does **not** use this rule — it has its own fixed refill logic.

B1 refill target is common to all strategies:

- TargetCash = Sum of raw CSV S1Spend/S2Spend for the next N years
- RefillAmount = max(0, TargetCash − CurrentB1)

If the source bucket cannot fully cover the refill, a **partial refill** occurs.

---

# 🔸 Strategy 1 — Momentum (Cash + ETF)

Momentum has **two buckets**:

- **Cash** (B1)
- **ETF** (B2)

### **When does Momentum refill?**

Momentum refills **only if the previous year’s market return was positive**:

- If PreviousReturn > 0 → refill allowed
- Else → no refill

** EXCEPTION ** refill is forced in first year of plan (as year 1 is the start point)

This is the core Momentum rule:  
**never sell ETF after a bad year**.

### **How much does Momentum refill?**

Momentum uses the shared refill‑years logic:

- If health is strong → refill **3 years**
- Otherwise → refill **1 year**

### **Where does the refill come from?**

Always from **ETF → Cash**.

- Refill = min(RefillAmount, ETF)
- ETF -= Refill
- Cash += Refill


ETF never goes negative.

---

# 🔸 Strategy 2 — Time Buckets (B1 → B2 → B3)

Time Buckets uses **strict time segmentation**, but its refill logic is **not blind**.  
Refill is attempted every year, but the **amount** and **caps** depend on:

- FundingState (Normal / Good / Bad / ExtremeBad)
- Return thresholds (large negative years halve caps)
- Bucket availability (B2/B3)

This makes Time Buckets **state‑aware**, **return‑aware**, and **bucket‑aware**.

---

## **When does Time Buckets refill?**

Refill is **attempted every year**, but the *behaviour* depends on FundingState:

- **Normal / Good** → full refills allowed  
- **Bad** → both B1 and B2 refills at 2 years
- **ExtremeBad** →  both B1 and B2 refills at 1 years

There is **no Momentum timing** (no “skip refill after bad year”).  
Refill always runs, but its *strength* adapts to conditions.

---

## **What is refilled?**

Time Buckets refills **both B1 and B2** toward their **time‑based targets**:

- **B1 target** = next *Bucket1Years* of spend  
- **B2 target** = following *Bucket2Years* of spend  

Targets are computed from **raw CSV S1Spend/S2Spend** (pre‑guardrail, pre‑bonus).

---

## **No rebalance step**

Time Buckets does **not** rebalance B2/B3.  
It only refills B1 and B2 according to the state‑aware rules above.

---

# 🔸 Strategy 3 — Hybrid (B1 + B2 + B3)

Hybrid combines:

- **Momentum timing** (when to refill)  
- **Bucket‑aware selling** (what to sell)  
- **Post‑refill rebalancing** (unique to Hybrid)

### **When does Hybrid refill?**

Hybrid uses **Momentum’s refill timing**, but with a blended return:

PrevReturnHybrid = (PrevBondReturn × AllocationB2) +
(PrevEquityReturn × AllocationB3)

- If PrevReturnHybrid > 0 → refill allowed
- Else → no refill

This avoids selling B2/B3 after a bad year.

** EXCEPTION ** refill is forced in first year of plan (as year 1 is the start point)

### **How much does Hybrid refill?**

Same refill‑years logic as Momentum:

- Strong health → refill **3 years**
- Otherwise → refill **1 year**

### **Where does Hybrid refill from?**

Hybrid chooses the **source bucket** based on relative asset strength:

- If EquityReturn > BondReturn → refill from B3 (equities)
- Else → refill from B2 (bonds)

This ensures:

- Sell the **stronger** asset  
- Avoid selling the weaker one  
- Never sell both in the same refill step

Partial refill allowed.

### **Hybrid’s extra step — Rebalance B2/B3**

After refill (but before returns), Hybrid performs a **rebalance**:

Rebalance is checked each year to ensure allocations remain "in tollerance".

- If drift < tolerance
    - Skip rebalance
- ElseIF the asset being sold is down:
    - Rebalance to closest in tollerance point (e.g. 65/35 or 55/45)
- Else:
    - Full rebalance (e.g. 60/40)

This is **unique to Hybrid**.  
Momentum and Time Buckets do not explicitly rebalance (momentum rebalances indirectly by virtue of usikng 60/40 ETF which will relebance itself).

--

# Summary — Refill behaviour by strategy

| Strategy | Refill Timing | Refill Target | Refill Source | Rebalance? |
|----------|----------------|----------------|----------------|-------------|
| **Momentum** | Only if previous return > 0 | 1–3 years (health‑based) | ETF → Cash | No |
| **Time Buckets** | Every year | B1 & B2 time‑based targets | B3 → B2 → B1 | No |
| **Hybrid** | Only if blended return > 0 | 1–3 years (health‑based) | Stronger of B2/B3 → B1 | **Yes** (maintain B2/B3 split) |

---


## Step 9 — Apply market return (end of year)

At the **end of each simulation year (31st December)**, market returns are applied to the **invested buckets only**.  
Cash buckets **never** receive market returns.

Although the timing is identical across all strategies, the **buckets that receive returns differ**.

---

## 🔹 Overview — What “apply returns” means

- **Cash (B1)** → **unchanged**  
- **Bond bucket (B2)** → grows by **(1 + rBond)**  
- **Equity bucket (B3)** → grows by **(1 + rEquity)**  
- **Momentum ETF** → grows by **(1 + rETF6040)**

Returns are always applied **after withdrawals and after refill**, and represent the growth of the invested assets during the year.

---

# 🔸 Strategy 1 — Momentum (Cash + ETF)

Momentum has:

- **Cash** (no return)
- **ETF** (single blended 60/40 global ETF)

### Return rule
- ETF = ETF × (1 + etf6040_realReturn)
- Cash is unchanged


Momentum applies **only one return value** per year — the blended ETF return.

---

# 🔸 Strategy 2 — Time Buckets (B1, B2, B3)

Time Buckets uses:

- **B1** — cash (no return)
- **B2** — bonds
- **B3** — equities

### Return rule
- B2 = B2 × (1 + GlobalBonds_realReturn)
- B3 = B3 × (1 + GlobalTracker_realReturn)
- B1 is unchanged


This reflects the strategy’s explicit separation of defensive and growth assets.

---

# 🔸 Strategy 3 — Hybrid (B1, B2, B3)

Hybrid also uses:

- **B1** — cash (no return)
- **B2** — bonds
- **B3** — equities

### Return rule

- B2 = B2 × (1 + GlobalBonds_realReturn)
- B3 = B3 × (1 + GlobalTracker_realReturn)
- B1 is unchanged


Hybrid applies returns **after**:

1. Withdrawals  
2. Refill  
3. **Hybrid‑only B2/B3 rebalance**

This ensures returns apply to the **post‑rebalance** bucket sizes.

---

# Summary — Return behaviour by strategy

| Strategy | Cash Return | Bond Return | Equity Return | Notes |
|----------|-------------|-------------|---------------|--------|
| **Momentum** | None | n/a | n/a | Single blended ETF return applied to ETF bucket |
| **Time Buckets** | None | Yes (B2) | Yes (B3) | Pure time‑segmented buckets |
| **Hybrid** | None | Yes (B2) | Yes (B3) | Returns applied after Hybrid‑only rebalance |

---

Returns always represent **end‑of‑year growth**, and are the final step before the next year begins.

---

### 🆕 Final Cash Sweep (no future SIPP spend)

Once a SIPP reaches a point in the plan where **no future S1Spend/S2Spend remains**, the SIPP has effectively transitioned into a **pure legacy pot**.  
At this point, **cash is no longer required** for runway or buffer management.

To prevent long‑term drag from idle cash, the engines perform a **one‑time end‑of‑plan sweep** that moves all remaining cash into the appropriate *risk bucket(s)* for that strategy.

This sweep happens **after withdrawals and refill**, but **before the next year’s return cycle**.

The behaviour differs by strategy.

---

## 🔸 Strategy 1 — Momentum (Cash + ETF)

Momentum has only two buckets:

- **Cash**
- **ETF (60/40)**

When a SIPP has **no future S1Spend/S2Spend**:

- If Cash > 0:
    - ETF += Cash
    - Cash = 0


This ensures the remaining pot continues to receive **ETF returns** in all subsequent years.

- Sweep happens **once**, at the first year where future spend = 0  
- After the sweep, the SIPP becomes a **fully invested ETF pot**

---

## 🔸 Strategy 2 — Time Buckets (B1, B2, B3)

Time Buckets uses:

- **B1** = cash  
- **B2** = bonds  
- **B3** = equities  

When a SIPP has **no future spend**, the engine performs a sweep that moves **all B1 cash into B3**:

- If B1 > 0:
    - B3 += B1
    - B1 = 0


Why B3?

- B3 is the **long‑term growth bucket**
- With no future spending, there is no need for B1 or B2 runway
- The remaining pot should behave like a **fully invested legacy portfolio**

After the sweep:

- B1 = 0  
- B2 remains invested in bonds  
- B3 holds all growth assets + swept cash

---

## 🔸 Strategy 3 — Hybrid (B1, B2, B3)

Hybrid uses:

- **B1** = cash  
- **B2** = bonds  
- **B3** = equities  

When a SIPP has **no future spend**, Hybrid performs the same sweep as Time Buckets:

- If B1 > 0:
    - B3 += B1
    - B1 = 0


This matches the Hybrid philosophy:

- B1 is only for near‑term spending  
- With no future spending, B1 should be eliminated  
- The remaining pot should be fully invested in **risk assets** (B2/B3)

After the sweep:

- B1 = 0  
- B2 and B3 continue to receive their respective returns  
- No further refills or rebalancing occur (because no future spend exists)

---

## Summary — Final Cash Sweep by Strategy

| Strategy | Sweep Trigger | Sweep Action | Result |
|----------|---------------|--------------|--------|
| **Momentum** | No future S1/S2 spend | Cash → ETF | Fully invested ETF pot |
| **Time Buckets** | No future S1/S2 spend | B1 → B3 | Fully invested B2/B3 pot |
| **Hybrid** | No future S1/S2 spend | B1 → B3 | Fully invested B2/B3 pot |

---

### Why this rule exists

Without the sweep:

- Cash would sit idle for decades  
- Long‑term returns would be artificially suppressed  
- Legacy values would be understated  
- Strategy comparisons would be distorted

The sweep ensures that once a SIPP becomes a **legacy‑only pot**, it behaves like a **properly invested portfolio**, not a partially idle one.

---

## Step 10 — End‑of‑year state finalised

At this point, the annual cycle completes, and this position becomes the start‑of‑year state for the next iteration.

---

# 🧭 Principles (core truths of the model)

These principles summarise the model’s behaviour.  
For deterministic rules, see **Authoritative Year Execution Order**.

## 1. Every pound withdrawn is classified as exactly one of:

- **SIPP‑funded objectives (after guardrails)**  
- **Bonus uplift**

There is no third category.

---

## 2. Legacy is separate from withdrawals

Legacy = remaining SIPP balances at end of year 40.

---

## 3. Buckets are internal SIPP components

Internal reallocations (refill, rescue, BX→BY) are **not** withdrawals, just movements WITHIN the SIPP.

---

## 4. Rescue is allowed (S1 → S2)

Rescue:

- Uses S1’s sequencing rules  
- Does not recalc S1’s guardrails or bonus  
- May still result in unmet objectives  

---

# 🧪 Backtesting Outcome Classification

Each simulation run produces a **rolling backtest dataset** across multiple historical start years.  
To summarise performance consistently, each run is classified into one of four **End States**.

These classifications apply to **both full and partial simulations** and are evaluated per start year.

---

## 🧭 End State definitions

### 🟢 PerfectPass

A simulation is classified as **PerfectPass** when all of the following are true:

- The simulation survives the full time horizon (no depletion)
- Objective coverage is strong:
  - O1 ≥ 95%
  - O2 ≥ 80%
  - O3 ≥ 70%
- Net outcome is positive:
  
      TotalActualPlusLegacy > TotalPlanned

This represents a scenario where:
- All core objectives are fully resilient
- Lifestyle and luxury objectives are strongly supported
- The system ends in surplus after accounting for legacy

---

### 🟡 Pass

A simulation is classified as **Pass** when:

- The simulation survives the full time horizon (no depletion)
- Objective coverage meets minimum acceptable thresholds:
  - O1 ≥ 85%
  - O2 ≥ 50%
  - O3 ≥ 30%
- Net outcome is positive:
  
      TotalActualPlusLegacy > TotalPlanned

This represents:
- Fully viable retirement outcomes
- Some reduction in discretionary objectives during stress periods
- No structural failure of essential spending

---

### 🔴 Fail

A simulation is classified as **Fail** when:

- The simulation either:
  - Survives the full horizon but fails objective or net thresholds, OR
  - Experiences depletion after at least 80% of the simulation horizon

This represents:
- Plan is broadly functional but materially weakened
- One or more objectives significantly underfunded
- Late-stage or partial structural stress

---

### ⚫ BadFail

A simulation is classified as **BadFail** when:

- The portfolio depletes before 80% of the simulation horizon

This represents:
- Early structural failure of the retirement plan
- Insufficient resilience to sequence-of-returns risk
- Fundamental breakdown of sustainability assumptions

---

## 📊 Classification logic (deterministic order)

End states are evaluated in the following strict order:

1. PerfectPass  
2. Pass  
3. BadFail  
4. Fail (default fallback)

If a higher category matches, lower categories are not evaluated.

---

## 📌 Key principles

- Classification is based on **final simulation outcomes only**
- All thresholds are deterministic and fixed
- Net outcome includes **SIPP value + legacy**
- Objective percentages reflect **delivered vs planned funding**
- Depletion timing is measured as a fraction of total simulation length

---

## 📈 Interpretation in backtests

When aggregated across rolling start years:

- **PerfectPass count** → measures robustness under all historical conditions
- **Pass count** → acceptable but not optimal outcomes
- **Fail count** → stress sensitivity or late-life weakness
- **BadFail count** → structural fragility under adverse sequences

These classifications provide a compact view of **strategy resilience across market regimes**.

---

---

# 📂 Input files

## 🔧 Configuration (Phase 1 — Required Parameters)

The simulator supports an external configuration file (`config.json`) that defines
key model parameters which were previously hard‑coded.  
This allows the model to be adjusted without recompilation and prepares the engine
for future scenario‑driven extensions.

Only the **required** parameters are included in this phase.  
All other behaviour remains exactly as defined in this specification.

---

### 📂 Config file format (Phase 1)

```json
{
  "SimulationStartYear": 1973,
  "Sipp1StartingBalance": 1284393.27,
  "Sipp2StartingBalance": 759165.85,

  "SpendPlanFile": ".\\data\\sipp-spend-plan.csv",
  "MarketReturnsFile": ".\\data\\market-returns.csv",

  "LiabilityDiscountRate": 0.01,

  "Guardrails": {
    "Tier4Threshold": 0.70,
    "Tier3Threshold": 0.80,
    "Tier2Threshold": 0.90,
    "Tier1Threshold": 1.00
  },

  "Bonus": {
    "StartThreshold": 1.10,
    "Tier2Threshold": 1.20,
    "Tier3Threshold": 1.30,
    "Tier4Threshold": 1.40,
    "Tier5Threshold": 1.50,

    "PctTier1": 0.02,
    "PctTier2": 0.03,
    "PctTier3": 0.04,
    "PctTier4": 0.05,
    "PctTier5": 0.06
  },

  "Momentum": {
    "CashBufferYearsHighHealth": 3,
    "CashBufferYearsLowHealth": 1,
    "CashBufferHealthThreshold": 1.20
  },

  "TimeBuckets": {
    "Bucket1Years": 4,
    "Bucket2Years": 8,

    "FundingStateExtremeBadThreshold": 0.80,
    "FundingStateBadThreshold": 0.90,
    "FundingStateGoodThreshold": 1.10
  },

  "Hybrid": {
    "AllocationB2": 0.40,
    "AllocationB3": 0.60,

    "B2HealthGoodThreshold": 1.00,
    "B3HealthGoodThreshold": 1.00,

    "B2HealthDiscountAdjustment": -0.01,
    "B3HealthDiscountAdjustment": -0.03,

    "DriftTolerance": 0.05,
    "EquitySellThreshold": 0.00,
    "BondSellThreshold": 0.00
  }
}

```

## 1. `sipp-spend-plan.csv`

Defines planned spending for each year.

| Column | Meaning |
|--------|---------|
| Year | 1–40 |
| O1 | Essential objectives |
| O2 | Lifestyle objectives |
| O3 | Luxury objectives |
| S1Spend | Planned SIPP withdrawal for S1 |
| S2Spend | Planned SIPP withdrawal for S2 |

Notes:

- O1/O2/O3 may exceed S1Spend + S2Spend  
- The difference is external funding  
- Guardrails apply only to the SIPP‑funded portion  

---

## 2. `market-returns.csv`

Defines ETF returns for each year (real, after inflation).  
Applied **at end of year**.

Format:

    Calendar,ETF6040 Real Return,GlobalBondETF_RealReturns,GlobalTrackerSharesETF_RealReturns,Inflation (Avg),Context
    1973,-0.1580,-0.0740,-0.3508,0.0830,Oil Crisis / Crash
    1974,-0.2580,-0.1410,-0.3819,0.1100,The Danger Zone
    ...

|Return type | data source |
|--------|---------|
|ETF6040 Real Return | https://curvo.eu/backtest/en/portfolio/60-40-portfolio--NoIgbADA9ALBAEAFA9gJwC4DNkBsCWyIANMKAJICiEEAQjADICsAmgJwAcAzMRAHRgBdEiHoBVCJwDs7dgEY5nWT14wBaoA |
|GlobalBondETF_RealReturns | Generated by copilot as averages for "global short and intermediate bonds, inflation |adjusted", as no appropriate curvo data provided.|
|GlobalTrackerSharesETF_RealReturns | https://curvo.eu/backtest/en/portfolio/spdr-msci-all-country-world-investable-market-ucits-etf-acc-ie00b3ylty66--NoIgygCgIgSgBAWTAYQJJwIIBst2QewFcA7AFwCcBPOAdX3KwBM5ViA3AUwGdSBDAIywdEvcgGsOpOAFU0AFTBwAonIBicABQYAxtoCUm1EoAMxgEIBmAJoAZOVYBsDvSAA0wUEdOXb9p24BGAF0QoA|

The Curvo data used was found by following the referenced page above, and downloading the CSV for Annual returns, the CSV  provides nominal returns (excluding inflation) by year. The Curvo data did not cover all years from 1973, so we needed to get gemini to "estimate" the figures for 1973 to 1985 (or so).

The data in our `market-returns.csv` is based on that the data sources above, but extended to factor in  UK inflation for the given year to provde a "real return".  E.g. if the ETF showed a growth of 6% in a given year, but that year inflation was 4%, then the real return would have been 2%.

### File format

The file must contain the following columns:

| Column            | Meaning                                      | Used by simulation |
|-------------------|----------------------------------------------|---------------------|
| `Calendar`        | Actual calendar year (e.g., 1973)            | **Yes**             |
| `Real Return`     | Real return for 60/40 ETF after inflation (decimal %)  | **Yes**             |
|GlobalBondETF_RealReturns | Real return for Global short and intermediate  bond ETF after inflation (decimal %) | **Yes**             |
|GlobalTrackerSharesETF_RealReturns | Real return for global tracker ETF after inflation (decimal %) | **Yes**             |
| `Inflation (Avg)` | Average inflation for the year (decimal %)   | No                  |
| `Context`         | Human‑readable description of the year       | No                  |

This file defines the **simulation window**, and the simulation will run only as long as return data is available.

## 3. Simulation length
Ideally, the simulation starts at the year specified by SimulationStartYear and runs for all years defined within `sipp-spend-plan.csv` (e.g. 40, for a 40 year plan).  However the simulation length can be capped down if needed based on the number of usable rows which are available within the `market-returns.csv` file.  E.g. if the SimulationStartYear is set to 2020 and there is only market return data available until 2025, then the length the simulation can run for is 6 years max (2020 to 2025 inclusive) even if the plan itself had 40 plan years

`SimulationYears = min(PlanYears, ReturnYearsRemainingFromStartYear)`

---

# 📊 Summary output explained

## 1. Final joint summary

Shows:

- Plan Simulation Years (e.g. Simulation window: 1973 → 2012)
- Planned withdrawals  
- Actual withdrawals  
- Legacy  
- Net difference  

---

## 2. Objective fulfilment table

Shows:

- Planned  
- External  
- SIPP Plan  
- SIPP Actual  
- Delivered  
- %Funded  

Unmet withdrawals appear as reduced SIPP Actual.

---

## 3. Bonus / legacy

Shows:

- Total bonus uplift  
- Final SIPP balances  

---

## 4. Reconciliation check

Ensures:

    (Base withdrawals) + (Bonus) = (S1Actual + S2Actual)

---

# 🖥️ Usage

1. Place both CSV files and config in the executable directory.  
2. Run the program.  
3. Choose strategy:  
   - `1` → Momentum Rebalance  
   - `2` → Dynamic Time Buckets  
   - `3` → Hybrid
   - `4` → Momentum only Backtest
   - `5` → Dynamic Time Buckets only Backtest
   - `6` → Hybrid only Backtest
   - `7` → Combined Backtest
   
   Note. append 'v' to choice (e.g. `1v` or `2v`) → verbose mode  

---
