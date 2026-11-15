import { useId } from "react";
import type { ReactNode } from "react";
import { motion } from "motion/react";
import type { Fold, Part } from "../parts";
import { CardPart } from "./CardPart";
import { ChangedPart } from "./ChangedPart";
import { GlucosePart, MainHint } from "./GlucosePart";
import { InsightsPart } from "./InsightsPart";
import { MapPart } from "./MapPart";
import { PartBoundary } from "./PartBoundary";
import type { PartsContext } from "./nativeParts";
import { glucoseOn, insightsShown } from "./partData";
import { RecoveryPart } from "./RecoveryPart";
import { TrainingPart } from "./TrainingPart";
import "./parts.css";

// One native part in its cell (nativeParts.tsx): the working piece Home draws,
// laid out by the cell's size, saying quietly what isn't there.
export function NativePart({
  part,
  fold,
  context,
}: {
  part: Part;
  fold: Fold;
  context: PartsContext;
}) {
  const from = `part-${useId()}`;
  const { data, blocked, session } = context;
  if (!data) return null;
  const shown = insightsShown(context.insights, blocked);
  const insight = (id: string) => context.openInsight(id, from);
  let content: ReactNode;
  switch (part.kind) {
    case "glucose":
      content = glucoseOn(data) ? (
        <GlucosePart
          data={data}
          state={context.state}
          blocked={blocked}
          readData={session?.data}
          onOpen={() => context.openPanel("glucose", from)}
        />
      ) : (
        <div className="main-hint">
          <MainHint
            dashboards={context.dashboards.list.length > 0}
            onSources={context.openSources}
          />
        </div>
      );
      break;
    case "recovery":
      content = (
        <RecoveryPart
          data={data}
          blocked={blocked}
          onOpen={() => context.openPanel("recovery", from)}
        />
      );
      break;
    case "training":
      content = (
        <TrainingPart
          data={data}
          blocked={blocked}
          demo={context.demo}
          onOpen={() => context.openPanel("training", from)}
        />
      );
      break;
    case "changed":
      content = (
        <ChangedPart
          nodes={blocked ? [] : context.sky.overview.nodes}
          edges={context.sky.overview.edges}
          sources={context.sources}
          paused={context.paused}
          shown={shown}
          onStar={context.openStar}
          onSources={context.openSources}
          onPanel={() => context.openPanel("changed", from)}
          onInsight={insight}
          quiet
        />
      );
      break;
    case "insights":
      content = (
        <InsightsPart
          shown={shown}
          insights={blocked ? [] : context.insights.insights}
          onInsight={insight}
        />
      );
      break;
    case "card":
      content = session && part.dashboard && (
        <CardPart
          dashboard={part.dashboard}
          dashboards={context.dashboards}
          session={session}
          blocked={blocked}
          folded={fold !== "saved"}
          onOpen={context.openDashboard}
        />
      );
      break;
    case "map": {
      const { readMap, showEverything } = context;
      content = (
        <MapPart
          sky={context.sky}
          focus={part.focus ?? null}
          blocked={blocked}
          paused={context.paused}
          still={fold !== "saved"}
          read={readMap && ((cursor, signal) => readMap(part, cursor, signal))}
          onStar={context.openStar}
          onExplore={context.explore}
          onShowEverything={
            part.focus && showEverything
              ? () => showEverything(part)
              : undefined
          }
        />
      );
      break;
    }
    default:
      return null;
  }
  const className = `native-part part-${part.kind}`,
    folded = fold === "saved" ? undefined : fold;
  // A map or a card opens no panel; the tiles grow theirs from themselves.
  return part.kind === "map" || part.kind === "card" ? (
    <div className={className} data-fold={folded}>
      <PartBoundary>{content}</PartBoundary>
    </div>
  ) : (
    <motion.div layoutId={from} className={className} data-fold={folded}>
      <PartBoundary>{content}</PartBoundary>
    </motion.div>
  );
}
