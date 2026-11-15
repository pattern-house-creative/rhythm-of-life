import { describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DataView } from "../src/DataView";
import {
  calendar,
  dailyValue,
  headline,
  insulinSummary,
  meaningOf,
  niceTicks,
  ordered,
  readout,
  shortDate,
  show,
  starKind,
} from "../src/readingsModel";
import type { GraphSeries } from "../src/knowledge";

// Fictional days ending today, so names like "today" and "last night" hold
// whatever day the suite runs.
const today = new Date().toLocaleDateString("en-CA");
function series(
  metric: string,
  unit: string,
  values: number[],
  count = 1,
  label = metric,
): GraphSeries {
  const dates = calendar(today, values.length);
  const days = values.map((mean, i) => ({
    date: dates[i],
    min: mean * 0.8,
    mean,
    max: mean * 1.2,
    count,
  }));
  return {
    metric,
    label,
    unit,
    latest: { value: values.at(-1)!, observedAt: null, date: today },
    days,
  };
}
const fortnight = (v: number) => Array.from({ length: 14 }, () => v);
function insulinStar() {
  return [
    series("basal_rate", "U/h", fortnight(0.9), 10, "Basal rate"),
    // 2 U a bolus, 9 a day: 18 U of bolus each day.
    series("bolus_delivered", "U", fortnight(2), 9, "Bolus delivered"),
    series(
      "auto_insulin_aggregate",
      "U",
      fortnight(6),
      1,
      "Auto insulin aggregate",
    ),
    // 20 g an entry, 9 a day: 180 g of carbs each day.
    series("carbohydrate_input", "g", fortnight(20), 9, "Carbohydrate input"),
  ];
}

describe("what each measurement means", () => {
  it("gives known measurements friendly names and a plain line", () => {
    const [basal, bolus, auto, carbs] = insulinStar();
    expect(meaningOf(auto).name).toBe("Auto-correction insulin");
    expect(meaningOf(carbs).name).toBe("Carbs entered");
    expect(meaningOf(bolus).name).toBe("Bolus insulin");
    expect(meaningOf(basal).name).toBe("Basal rate");
    expect(meaningOf(series("sleep_deep", "s", [5000])).name).toBe(
      "Deep sleep",
    );
    expect(meaningOf(auto).about).toMatch(/SmartGuard/);
  });

  it("reads additive measurements as daily totals and rates as their average", () => {
    const [basal, bolus] = insulinStar();
    const day = bolus.days[0];
    expect(dailyValue(day, meaningOf(bolus))).toBe(18);
    expect(dailyValue(basal.days[0], meaningOf(basal))).toBe(0.9);
    expect(headline(bolus, meaningOf(bolus), today)).toMatchObject({
      value: 18,
      caption: "per day · 14-day average",
      aside: "Today 18.0 U",
    });
    expect(headline(basal, meaningOf(basal), today).caption).toBe(
      "average · 14 days",
    );
  });

  it("falls back gracefully for a measurement it has never seen", () => {
    const grams = series("fiber_input", "g", [12, 14], 3, "Fiber input");
    const rate = series("glucose_ketones", "mmol/L", [0.3, 0.4], 4, "Ketones");
    const once = series("mood_score", "score", [7, 8], 1, "Mood score");
    expect(meaningOf(grams)).toMatchObject({
      name: "Fiber input",
      daily: "total",
      section: "More readings",
    });
    expect(meaningOf(rate)).toMatchObject({ daily: "average", band: true });
    expect(meaningOf(once).headline).toBe("latest");
    expect(show(0.35, "mmol/L")).toEqual({ value: "0.35", unit: "mmol/L" });
    // Known measurements come first, new ones keep their order after.
    expect(
      ordered([grams, ...insulinStar()])
        .map((s) => s.metric)
        .at(-1),
    ).toBe("fiber_input");
  });

  it("says numbers in their own units", () => {
    expect(show(26460, "s").value).toBe("7h 21m");
    expect(show(2700, "s").value).toBe("45 min");
    expect(show(20.46, "U")).toEqual({ value: "20.5", unit: "U" });
    expect(show(0.9, "U/h")).toEqual({ value: "0.90", unit: "U/h" });
    expect(show(9801.4, "count", { unit: "steps" })).toEqual({
      value: "9,801",
      unit: "steps",
    });
    expect(show(8046.72, "m", undefined, true)).toEqual({
      value: "5.00",
      unit: "mi",
    });
    expect(show(8000, "m", undefined, false)).toEqual({
      value: "8.00",
      unit: "km",
    });
    expect(show(52.4, "beats/min")).toEqual({ value: "52", unit: "bpm" });
    expect(show(3.14, "score").value).toBe("3.1");
  });

  it("adds up workouts over the last week, and reads one day under the cursor", () => {
    const time = series(
      "activity_duration",
      "s",
      [3600, 0, 1800, 2400, 0, 0, 3000, 4000, 0, 3600, 0, 1800, 0, 2700],
    );
    // Rest days have no reading at all.
    time.days = time.days.filter((d) => d.mean > 0);
    const m = meaningOf(time);
    const h = headline(time, m, today);
    expect(h.caption).toBe("last 7 days");
    expect(h.value).toBe(4000 + 3600 + 1800 + 2700);
    expect(h.aside).toBe("4 workouts · 3h 00m the week before");
    const [bolus] = insulinStar().slice(1);
    const r = readout(bolus, meaningOf(bolus), today);
    expect(r.value).toBe(18);
    expect(r.aside).toMatch(/^9 boluses/);
    expect(readout(time, m, calendar(today, 2)[0]).aside).toBe(
      "No reading this day",
    );
  });

  it("steps an hour axis in whole hours", () => {
    expect(niceTicks(0, 30000, 4, "s")).toEqual([0, 10800, 21600]);
    expect(niceTicks(0, 23, 4)).toEqual([0, 10, 20]);
  });
});

describe("a star at a glance", () => {
  it("splits insulin into bolus and auto-correction, with carbs and the 14 days before", () => {
    const star = insulinStar();
    const s = insulinSummary(star, today);
    expect(s.bolusPerDay).toBe(18);
    expect(s.autoPerDay).toBe(6);
    expect(s.insulinPerDay).toBe(24);
    expect(s.carbsPerDay).toBe(180);
    expect(s.carbsPerUnit).toBe(10);
    expect(s.autoShare).toBe(0.25);
    expect(s.basal).toBeCloseTo(0.9);
    // Only 14 days of fictional history: nothing to compare with.
    expect(s.previous).toBeNull();
    expect(s.days).toHaveLength(14);
  });

  it("chooses a summary from what a star measures, not its name", () => {
    expect(starKind(insulinStar())).toBe("insulin");
    expect(starKind([series("sensor_glucose", "mg/dL", [140], 288)])).toBe(
      "glucose",
    );
    expect(starKind([series("sleep_duration", "s", [26000])])).toBe("sleep");
    expect(starKind([series("steps", "count", [9000])])).toBe("daily");
    expect(starKind([series("activity_duration", "s", [3000])])).toBe(
      "training",
    );
    expect(starKind([series("weight", "kg", [70])])).toBe("other");
  });
});

// jsdom lays nothing out; give a chart a width to move across.
function lay(el: Element, width = 280) {
  el.getBoundingClientRect = () =>
    ({
      left: 0,
      top: 0,
      width,
      height: 64,
      right: width,
      bottom: 64,
    }) as DOMRect;
}

describe("the readings screen", () => {
  it("opens on the star at a glance, with each card in plain words", async () => {
    const read = vi.fn().mockResolvedValue(insulinStar());
    render(<DataView id="fictional-insulin" read={read} />);
    const glance = await screen.findByRole("region", { name: "At a glance" });
    expect(within(glance).getByText("Insulin per day")).toBeInTheDocument();
    expect(within(glance).getByText("24.0")).toBeInTheDocument();
    expect(
      within(glance).getByText("Carbs entered per day"),
    ).toBeInTheDocument();
    expect(within(glance).getByText("180")).toBeInTheDocument();
    expect(
      within(glance).getByText(/Auto-correction was 25% of all bolus insulin/),
    ).toBeInTheDocument();
    for (const name of [
      "Bolus insulin",
      "Auto-correction insulin",
      "Basal rate",
      "Carbs entered",
    ])
      expect(screen.getByRole("heading", { name })).toBeInTheDocument();
    // Insulin and food are their own sections.
    expect(screen.getByRole("region", { name: "Insulin" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Food" })).toBeInTheDocument();
  });

  it("reads a day under the keyboard, a mouse or a finger, across every card", async () => {
    const user = userEvent.setup();
    render(
      <DataView
        id="fictional-insulin"
        read={vi.fn().mockResolvedValue(insulinStar())}
      />,
    );
    const bolus = await screen.findByRole("slider", {
      name: "Bolus insulin by day",
    });
    const card = bolus.closest("li")!;
    const carbs = screen
      .getByRole("slider", { name: "Carbs entered by day" })
      .closest("li")!;
    // Keyboard: the arrows step through days with readings.
    bolus.focus();
    await user.keyboard("{ArrowLeft}");
    expect(bolus).toHaveAttribute(
      "aria-valuetext",
      expect.stringMatching(/18\.0 U, 9 boluses/),
    );
    expect(within(card).getByText(/day total/)).toBeInTheDocument();
    // The other cards follow the same day.
    expect(within(carbs).getByText(/day total/)).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(
      within(card).getByText("per day · 14-day average"),
    ).toBeInTheDocument();
    // A mouse reads wherever it hovers, and lets go when it leaves.
    lay(bolus);
    fireEvent.pointerMove(bolus, { pointerType: "mouse", clientX: 2 });
    expect(
      within(card).getByText(new RegExp(shortDate(calendar(today, 14)[0]))),
    ).toBeInTheDocument();
    fireEvent.pointerLeave(bolus, { pointerType: "mouse" });
    expect(
      within(card).getByText("per day · 14-day average"),
    ).toBeInTheDocument();
    // A finger reads only once it moves sideways, and its day stays.
    fireEvent.pointerDown(bolus, {
      pointerType: "touch",
      pointerId: 3,
      clientX: 100,
    });
    fireEvent.pointerMove(bolus, {
      pointerType: "touch",
      pointerId: 3,
      clientX: 103,
    });
    expect(
      within(card).getByText("per day · 14-day average"),
    ).toBeInTheDocument();
    fireEvent.pointerMove(bolus, {
      pointerType: "touch",
      pointerId: 3,
      clientX: 279,
    });
    fireEvent.pointerUp(bolus, { pointerType: "touch", pointerId: 3 });
    expect(within(card).getByText(/day total/)).toBeInTheDocument();
    expect(bolus).toHaveAttribute("aria-valuenow", "13");
    // A drag is not a click: the card stays closed.
    fireEvent.click(bolus);
    expect(
      within(card).getByRole("button", { name: "Open Bolus insulin" }),
    ).toHaveAttribute("aria-expanded", "false");
  });

  it("opens a card in place into a larger chart with axes, and closes it again", async () => {
    const user = userEvent.setup();
    render(
      <DataView
        id="fictional-insulin"
        read={vi.fn().mockResolvedValue(insulinStar())}
      />,
    );
    const heading = await screen.findByRole("heading", {
      name: "Carbs entered",
    });
    const card = heading.closest("li")!;
    await user.click(within(card).getByText(/Carbs you told the pump/));
    expect(
      within(card).getByRole("button", { name: "Close Carbs entered" }),
    ).toHaveAttribute("aria-expanded", "true");
    expect(within(card).getByText("Lowest day")).toBeInTheDocument();
    expect(within(card).getByText("Days with readings")).toBeInTheDocument();
    expect(within(card).getByText("Average day").nextSibling).toHaveTextContent(
      "180 g",
    );
    // Axis labels on the opened chart.
    expect(card.querySelectorAll(".chart-tick").length).toBeGreaterThan(1);
    // Enter on the chart closes it too, without a modal anywhere.
    const chart = within(card).getByRole("slider");
    chart.focus();
    await user.keyboard("{Enter}");
    expect(within(card).queryByText("Lowest day")).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("offers longer spans only when the readings reach back that far", async () => {
    const user = userEvent.setup();
    const long = [
      series(
        "sensor_glucose",
        "mg/dL",
        Array.from({ length: 90 }, (_, i) => 130 + (i % 7)),
        288,
        "Sensor glucose",
      ),
    ];
    const { unmount } = render(
      <DataView
        id="fictional-glucose"
        read={vi.fn().mockResolvedValue(long)}
      />,
    );
    const spans = await screen.findByRole("group", {
      name: "Days on each chart",
    });
    expect(
      within(spans)
        .getAllByRole("button")
        .map((b) => b.textContent),
    ).toEqual(["14 days", "30 days", "90 days"]);
    await user.click(within(spans).getByRole("button", { name: "90 days" }));
    expect(
      screen.getByRole("slider", { name: "Sensor glucose by day" }),
    ).toHaveAttribute("aria-valuemax", "89");
    expect(screen.getByText("Days with sensor data")).toBeInTheDocument();
    unmount();
    render(
      <DataView
        id="fictional-insulin"
        read={vi.fn().mockResolvedValue(insulinStar())}
      />,
    );
    await screen.findByRole("region", { name: "At a glance" });
    expect(
      screen.queryByRole("group", { name: "Days on each chart" }),
    ).not.toBeInTheDocument();
  });

  it("summarizes a star it has never seen from its own measurements", async () => {
    const star = [
      series("weight", "kg", [70.2, 70.4], 1, "Weight"),
      series("water_intake", "count", [6, 8], 1, "Water intake"),
    ];
    render(
      <DataView id="fictional-other" read={vi.fn().mockResolvedValue(star)} />,
    );
    const glance = await screen.findByRole("region", { name: "At a glance" });
    expect(glance).toHaveTextContent("2 MEASUREMENTS · 2 DAYS");
    expect(within(glance).getByText("70.4")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Weight" })).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Water intake" }),
    ).toBeInTheDocument();
  });

  it("drops what it read when the star changes", async () => {
    let resolve: (s: GraphSeries[]) => void = () => {};
    const read = vi
      .fn()
      .mockImplementation(
        () => new Promise<GraphSeries[]>((r) => (resolve = r)),
      );
    const { rerender } = render(<DataView id="a" read={read} />);
    expect(screen.getByText(/Reading these measurements/)).toBeInTheDocument();
    await act(async () => resolve(insulinStar()));
    expect(
      screen.getByRole("heading", { name: "Bolus insulin" }),
    ).toBeInTheDocument();
    rerender(<DataView id="b" read={read} />);
    expect(
      screen.queryByRole("heading", { name: "Bolus insulin" }),
    ).not.toBeInTheDocument();
  });

  it("keeps a measurement with no days yet quiet instead of empty", async () => {
    const quiet = {
      ...series("bolus_delivered", "U", [2], 9),
      days: [],
      latest: null,
    };
    render(
      <DataView
        id="fictional-quiet"
        read={vi.fn().mockResolvedValue([quiet])}
      />,
    );
    const heading = await screen.findByRole("heading", {
      name: "Bolus insulin",
    });
    const card = heading.closest("li")!;
    expect(within(card).getByText("no recent reading")).toBeInTheDocument();
    expect(within(card).getByText("—")).toBeInTheDocument();
    expect(within(card).queryByRole("slider")).not.toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "At a glance" }),
    ).not.toBeInTheDocument();
  });
});
