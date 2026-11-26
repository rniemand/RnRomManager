/**
 * Where one file of a run stands. `waiting` hasn't been sent yet; `pending`/`sending` mirror the server's ticket;
 * `sent`, `failed` and `expired` are terminal server states; `unknown` means the server lost the ticket (B8).
 */
export type RunItemState =
	'waiting' | 'issuing' | 'pending' | 'sending' | 'sent' | 'failed' | 'expired' | 'unknown';
