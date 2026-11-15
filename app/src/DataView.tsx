import { useEffect, useState } from "react";
import { motion } from "motion/react";
import { Maximize2, Minimize2 } from "lucide-react";
import type { GraphSeries } from "./knowledge";
import { DayChart } from "./DayChart";
import { Figure, StarSummary } from "./StarSummary";
import {
  calendar,
  dailyValue,
  firstDate,
  headline,
  lastDate,
  meaningOf,
  more,
  ordered,
  readout,
  say,
  shortDate,
  show,
  spanDays,
} from "./readingsModel";
import type { Meaning } from "./readingsModel";
import "./DataView.css";

type Read = (id: string, signal?: AbortSignal) => Promise<GraphSeries[] | null>;

// A data star's readings, read from the engine while it is open and dropped when
// it closes: the star at a glance, then a card for each measurement in plain
// words, each with a chart you can read day by day and open up in place.
export function DataView({ id, read }: { id: string; read?: Read }) {
  const [loaded, setLoaded] = useState<{
    id: string;
    series: GraphSeries[] | null;
  } | null>(null);
  useEffect(() => {
    if (!read) return;
    const controller = new AbortController();
    read(id, controller.signal).then(
      (series) => setLoaded({ id, series }),
      () => {
        if (!controller.signal.aborted) setLoaded({ id, series: null });
      },
    );
    return () => {
      controller.abort();
      setLoaded(null);
    };
  }, [id, read]);
  const current = loaded?.id === id ? loaded : null;
  if (read && !current)
    return (
      <p className="source-content" aria-busy="true">
        Reading these measurements…
      </p>
    );
  if (!current?.series?.length)
    return (
      <p className="source-content">
        These measurements are unavailable right now. Try again later.
      </p>
    );
  return <Readings key={id} series={current.series} />;
}

const periods = [14, 30, 90];

function Readings({ series }: { series: GraphSeries[] }) {
  const end = lastDate(series);
  const first = firstDate(series);
  const span = end && first ? spanDays(first, end) : 0;
  // Only the spans the data actually reaches back to.
  const choices = periods.filter((_, i) => i === 0 || span > periods[i - 1]);
  const [period, setPeriod] = useState(span > 14 ? 30 : 14);
  // The day being read, shared by every chart on the screen.
  const [focus, setFocus] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const dates = end ? calendar(end, Math.min(period, Math.max(span, 1))) : [];
  const sections: { name: string; items: GraphSeries[] }[] = [];
  for (const s of ordered(series)) {
    const name = meaningOf(s).section;
    const section = sections.find((x) => x.name === name);
    if (section) section.items.push(s);
    else sections.push({ name, items: [s] });
  }
  // New measurements gather at the end, after the ones we know.
  sections.sort((a, b) => Number(a.name === more) - Number(b.name === more));
  const titled = sections.length > 1;
  return (
    <div
      className="data-view"
      // A finger that lets go leaves its day showing; tapping off a chart
      // puts the cards back to their usual numbers.
      onPointerDown={(e) => {
        if (
          e.pointerType !== "mouse" &&
          !(e.target as Element).closest?.("[role=slider]")
        )
          setFocus(null);
      }}
    >
      {end && (
        <StarSummary
          series={series}
          end={end}
          focus={focus}
          setFocus={setFocus}
        />
      )}
      {end && (
        <div className="data-toolbar">
          <p className="data-hint">
            <span className="hint-pointer">
              Move across a chart to read any day · click a card to open it
            </span>
            <span className="hint-touch">
              Drag sideways on a chart to read any day · tap a card to open it
            </span>
          </p>
          {choices.length > 1 && (
            <div
              className="segmented"
              role="group"
              aria-label="Days on each chart"
            >
              {choices.map((p) => (
                <button
                  key={p}
                  aria-pressed={period === p}
                  onClick={() => setPeriod(p)}
                >
                  {p} days
                </button>
              ))}
            </div>
          )}
        </div>
      )}
      {sections.map((section) => (
        <section
          key={section.name}
          className="data-section"
          aria-label={titled ? section.name : undefined}
        >
          {titled && <h3 className="data-section-title">{section.name}</h3>}
          <ul className="data-grid">
            {section.items.map((s) => {
              const key = `${s.metric}:${s.unit}`;
              return (
                <Card
                  key={key}
                  series={s}
                  end={end}
                  dates={dates}
                  focus={focus}
                  setFocus={setFocus}
                  open={open === key}
                  toggle={() => {
                    setFocus(null);
                    setOpen((o) => (o === key ? null : key));
                  }}
                />
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}

function Card({
  series: s,
  end,
  dates,
  focus,
  setFocus,
  open,
  toggle,
}: {
  series: GraphSeries;
  end: string | null;
  dates: string[];
  focus: string | null;
  setFocus: (date: string | null) => void;
  open: boolean;
  toggle: () => void;
}) {
  const m = meaningOf(s);
  const h = end
    ? headline(s, m, end)
    : { value: null, caption: "no recent reading", aside: "" };
  const r = focus && dates.includes(focus) ? readout(s, m, focus) : null;
  const value = r ? r.value : h.value;
  const caption = r ? r.caption : h.caption;
  const aside = r ? r.aside : h.aside;
  const spoken = (v: number | null) =>
    v == null ? "no reading" : say(v, s.unit, m);
  const hasDays = s.days.some((d) => dates.includes(d.date));
  return (
    <motion.li
      layout="position"
      transition={{ type: "spring", stiffness: 320, damping: 36 }}
      className={`data-card tone-${m.tone}${open ? " open" : ""}${r ? " reading" : ""}`}
      onClick={toggle}
    >
      <div className="data-card-top">
        <h4 className="data-name">{m.name}</h4>
        <button
          className="data-expand"
          aria-expanded={open}
          aria-label={`${open ? "Close" : "Open"} ${m.name}`}
          onClick={(e) => {
            e.stopPropagation();
            toggle();
          }}
        >
          {open ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
        </button>
      </div>
      <p className="data-about">{m.about}</p>
      <div className="data-reading">
        <Figure
          shown={value == null ? null : show(value, s.unit, m)}
          size={open ? "large" : undefined}
        />
        <span className="data-caption">{caption}</span>
      </div>
      {hasDays && (
        <DayChart
          series={s}
          meaning={m}
          dates={dates}
          focus={focus}
          setFocus={setFocus}
          large={open}
          label={`${m.name} by day`}
          valueText={
            r
              ? `${r.caption}: ${spoken(r.value)}${r.aside ? `, ${r.aside}` : ""}`
              : `${spoken(h.value)}, ${h.caption}`
          }
          onActivate={toggle}
        />
      )}
      <p className="data-aside">{aside || " "}</p>
      {open && <Period series={s} meaning={m} dates={dates} />}
    </motion.li>
  );
}

// An opened card's days at a glance: the usual day, the lowest and highest.
function Period({
  series: s,
  meaning: m,
  dates,
}: {
  series: GraphSeries;
  meaning: Meaning;
  dates: string[];
}) {
  const days = s.days.filter((d) => dates.includes(d.date));
  if (!days.length) return null;
  const valued = days.map((d) => ({ date: d.date, v: dailyValue(d, m) }));
  const low = valued.reduce((a, b) => (b.v < a.v ? b : a));
  const high = valued.reduce((a, b) => (b.v > a.v ? b : a));
  const mean = valued.reduce((n, d) => n + d.v, 0) / valued.length;
  const readings = days.reduce((n, d) => n + d.count, 0);
  return (
    <motion.dl
      className="data-period"
      initial={{ opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ delay: 0.12, duration: 0.3 }}
    >
      <div>
        <dt>{m.daily === "total" ? "Average day" : "Average"}</dt>
        <dd>{say(mean, s.unit, m)}</dd>
      </div>
      <div>
        <dt>Lowest day</dt>
        <dd>
          {say(low.v, s.unit, m)} <small>{shortDate(low.date)}</small>
        </dd>
      </div>
      <div>
        <dt>Highest day</dt>
        <dd>
          {say(high.v, s.unit, m)} <small>{shortDate(high.date)}</small>
        </dd>
      </div>
      <div>
        <dt>Days with readings</dt>
        <dd>
          {days.length} of {dates.length}
          {m.each && readings > days.length && (
            <small>
              {" "}
              {readings.toLocaleString("en-US")} {m.each[1]}
            </small>
          )}
        </dd>
      </div>
    </motion.dl>
  );
}
