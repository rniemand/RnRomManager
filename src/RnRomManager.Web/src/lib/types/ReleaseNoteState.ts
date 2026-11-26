/** B7: a release card's rendered note on the game page: loading, its text, or the load error. */
export type ReleaseNoteState = { loading: true } | { text: string } | { error: unknown };
