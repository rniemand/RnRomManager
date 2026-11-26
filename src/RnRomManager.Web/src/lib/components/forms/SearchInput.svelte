<script lang="ts">
	import type { ComponentProps } from 'svelte';
	import TextInput from './TextInput.svelte';

	type Props = Omit<ComponentProps<typeof TextInput>, 'type'>;

	let { value = $bindable(), ref = $bindable(), ...rest }: Props = $props();
</script>

<!-- No clear button or debounce: sites keep a sibling Clear button and their `debounced` call. -->
<TextInput type="search" bind:value bind:ref {...rest} />
