import { readingsFrom } from "../src/demoReadings";
import { sampleShapes } from "./sampleShapes";

// The glucose and Garmin panels' fictional readings (sampleShapes.ts).
export const sampleReadings = (id: string, today = new Date()) =>
  readingsFrom(sampleShapes, id, today);
