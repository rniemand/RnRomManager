// Bootstrap's own JS (spec §11): navbar collapse and dropdowns through the data API, modal focus trapping through
// `Modal`, tooltips through `Tooltip` (only via the `tooltip` action). The UMD bundle includes Popper, so there's no extra dependency. Imported only in the browser (layout, modal).
import bootstrap from 'bootstrap/dist/js/bootstrap.bundle.min.js';

export const Modal = bootstrap.Modal;
export const Tooltip = bootstrap.Tooltip;
