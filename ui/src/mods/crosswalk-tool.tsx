import { bindValue, trigger, useValue } from "cs2/api";

import styles from "./crosswalk-tool.module.scss";
import crosswalkIcon from "images/crosswalk.svg";

/**
 * Bindings published by CrosswalkToolUISystem. The group name is shared with the C# side and is the
 * only contract between the two halves — everything the panel shows is worked out over there,
 * because the frontend can see neither the tool system nor the selected entity.
 */
const GROUP = "crosswalkWidth";

const toolActive$ = bindValue<boolean>(GROUP, "toolActive", false);
const hasSelection$ = bindValue<boolean>(GROUP, "hasSelection", false);
const selectedPercent$ = bindValue<number>(GROUP, "selectedPercent", 100);
const globalPercent$ = bindValue<number>(GROUP, "globalPercent", 200);
const crossingNumber$ = bindValue<number>(GROUP, "crossingNumber", 0);
const crossingCount$ = bindValue<number>(GROUP, "crossingCount", 0);

/**
 * The toolbar button, and the small panel that appears beside it while the tool is running.
 *
 * The panel carries the widen and narrow buttons rather than leaving them to a keyboard shortcut,
 * because a mod tool cannot rely on the game enabling elevation input for it — see the comment at
 * the top of CrosswalkPickerToolSystem. Clicks on a junction are handled by the tool itself; these
 * buttons only act on whatever it has selected.
 */
export function CrosswalkToolButton(): JSX.Element {
  const toolActive = useValue(toolActive$);
  const hasSelection = useValue(hasSelection$);
  const selectedPercent = useValue(selectedPercent$);
  const globalPercent = useValue(globalPercent$);
  const crossingNumber = useValue(crossingNumber$);
  const crossingCount = useValue(crossingCount$);

  return (
    <>
      <button
        className={toolActive ? `${styles.button} ${styles.active}` : styles.button}
        title="Crossing width — set one junction at a time"
        onClick={() => trigger(GROUP, "toggleTool")}
      >
        <img className={styles.icon} src={crosswalkIcon} />
      </button>

      {toolActive && (
        <div className={styles.panel}>
          <div className={styles.title}>
            {hasSelection && crossingCount > 0
              ? `Crossing ${crossingNumber} of ${crossingCount}`
              : "Crossing width"}
          </div>

          {hasSelection ? (
            <>
              <div className={styles.row}>
                <button className={styles.step} onClick={() => trigger(GROUP, "narrow")}>
                  −
                </button>
                <span className={styles.value}>{selectedPercent}%</span>
                <button className={styles.step} onClick={() => trigger(GROUP, "widen")}>
                  +
                </button>
              </div>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "applyToJunction")}>
                Same for all {crossingCount} crossings here
              </button>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "resetSelected")}>
                Back to global ({globalPercent}%)
              </button>

              <div className={styles.hint}>
                Each crossing has its own ring. Point at one to make it live, then drag out from it
                to resize that crossing alone.
              </div>
            </>
          ) : (
            <div className={styles.hint}>
              Click a junction to select it. A ring appears on each of its crossings.
            </div>
          )}
        </div>
      )}
    </>
  );
}
