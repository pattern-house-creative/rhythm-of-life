// The coding agents these hooks know, and the one thing each differs in: which documented event carries a finished message
// and which ends a session (SB-P7-02). source: the name a capture is labelled with, one of the engine's session transcript
// sources (063's memory_transcript_sources), so what a hook saves never replaces a memory from another source (SB-P5-04). Every event and field here is read from that agent's own published reference, named
// in README.md with the date it was read. An agent is added only that way: VS Code and Antigravity wait, as README.md says.
//
// Only a field the agent documents as holding the message is read. The transcript file is never opened: a hook gets its
// path from every agent here, and Codex's reference says outright that its format is no stable interface.

const sessionId = (input) => input.session_id;
const workingFolder = (input) => (typeof input.cwd === "string" ? [input.cwd] : []);
// The folder a session started in, first, and the one it is in now. cwd follows the agent as it moves, so an agent that names
// the start in this variable has both checked: a session started elsewhere cannot get in by changing directory.
const startedIn = (variable) => (input, env) => {
  const [now] = workingFolder(input);
  if (now === undefined) return [];
  const start = env[variable];
  return typeof start === "string" && start && start !== now ? [start, now] : [now];
};

export const AGENTS = Object.freeze({
  "claude-code": {
    label: "Claude Code",
    source: "claude-code",
    turn: { event: "Stop", text: "last_assistant_message" },
    end: { event: "SessionEnd" },
    session: sessionId,
    folders: startedIn("CLAUDE_PROJECT_DIR"),
    // Its example sets timeout 10: a SessionEnd hook's own timeout raises the 1.5 seconds it gets by default.
    budgetMs: 8000,
  },
  codex: {
    label: "Codex",
    source: "codex-session",
    turn: { event: "Stop", text: "last_assistant_message" },
    end: { event: "SessionEnd" },
    session: sessionId,
    // Its reference names no variable for the folder a session started in, and calls cwd the session's working folder.
    folders: workingFolder,
    // Its SessionEnd has 1 second by default and 3 at most, whatever a hook asks.
    budgetMs: 2500,
  },
  cursor: {
    label: "Cursor",
    source: "cursor-session",
    turn: { event: "afterAgentResponse", text: "text" },
    end: { event: "sessionEnd" },
    // conversation_id is common to every session event; sessionEnd also has its session_id.
    session: (input) => input.conversation_id ?? input.session_id,
    folders: (input) => (Array.isArray(input.workspace_roots) ? input.workspace_roots : []),
    budgetMs: 8000,
  },
  "gemini-cli": {
    label: "Gemini CLI",
    source: "gemini-session",
    turn: { event: "AfterAgent", text: "prompt_response" },
    end: { event: "SessionEnd" },
    session: sessionId,
    folders: startedIn("GEMINI_PROJECT_DIR"),
    budgetMs: 8000,
  },
});

// One event as the hook needs it, or null when it is not one these hooks act on (an event nobody registered for, or input
// without a session). `continuing`: the agent is already running on because a stop hook told it to, so this turn is not
// a natural end; Claude Code, Codex and Gemini CLI all say so in stop_hook_active.
export function readEvent(agentName, input, env = {}) {
  const agent = Object.hasOwn(AGENTS, agentName) ? AGENTS[agentName] : null;
  if (!agent || input === null || typeof input !== "object" || Array.isArray(input)) return null;
  const session = agent.session(input);
  if (typeof session !== "string" || !session) return null;
  const folders = agent.folders(input, env);
  if (input.hook_event_name === agent.turn.event)
    return { kind: "turn", session, folders, text: input[agent.turn.text], continuing: input.stop_hook_active === true };
  if (input.hook_event_name === agent.end.event) return { kind: "end", session, folders };
  return null;
}
