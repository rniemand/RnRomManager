<script lang="ts">
	import { toasts } from '../stores/toasts.svelte';
	import Icon from './Icon.svelte';

	interface Props {
		value: string;
		/** What is copied, for the accessible name and the toast (e.g. `SHA1`). */
		label: string;
	}

	let { value, label }: Props = $props();

	async function copy(): Promise<void> {
		try {
			await navigator.clipboard.writeText(value);
			toasts.show(`${label} copied.`, 'success');
		} catch {
			toasts.show(`Couldn't copy the ${label}.`, 'danger');
		}
	}
</script>

<button
	type="button"
	class="btn btn-sm btn-link p-0 align-baseline"
	aria-label="Copy {label}"
	title="Copy {label}"
	onclick={copy}
>
	<Icon name="copy" />
</button>
