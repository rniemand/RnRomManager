<script lang="ts">
	import type { ComponentProps } from 'svelte';
	import Checkbox from './Checkbox.svelte';

	type Props = Omit<ComponentProps<typeof Checkbox>, 'type' | 'group' | 'value' | 'indeterminate'>;

	let { checked = $bindable(false), ref = $bindable(), wrapperClass, ...rest }: Props = $props();
</script>

<!-- A `form-switch` checkbox with `role="switch"`. -->
<Checkbox
	role="switch"
	wrapperClass={['form-switch', wrapperClass].filter(Boolean).join(' ')}
	bind:checked
	bind:ref
	{...rest}
/>
