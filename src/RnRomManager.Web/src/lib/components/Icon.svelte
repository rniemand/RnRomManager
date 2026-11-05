<script lang="ts">
	import { iconClass } from './icons';

	interface Props {
		/** Semantic name (`download`, `system-gb`, …), see `icons.ts`. */
		name: string;
		/** Accessible name; without it the icon is decorative (`aria-hidden`). */
		label?: string;
		class?: string;
	}

	let { name, label, class: extra = '' }: Props = $props();
</script>

{#if label}
	<i class="{iconClass(name)} {extra}" role="img" aria-label={label}></i>
{:else}
	<i class="{iconClass(name)} {extra}" aria-hidden="true"></i>
{/if}
